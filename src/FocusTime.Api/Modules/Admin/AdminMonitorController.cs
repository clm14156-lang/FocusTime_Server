using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FocusTime.Api.Modules.Admin;

[ApiController]
[Route("api/v1/admin/monitor")]
[Authorize(Policy = AdminAccess.Policy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminMonitorController(AdminMonitor monitor) : ControllerBase
{
    [HttpGet]
    public Task<MonitorSnapshot> Get(CancellationToken ct) => monitor.CheckAsync(ct);
}

public sealed record MonitorService(string Id, string Status, double? LatencyMs, double? ErrorRate,
    string MetricLabel, string Note);
public sealed record MonitorIncident(string Id, string Service, string Type, string Severity, string Error,
    string? Impact, long OccurredAt, long? RecoveredAt);
public sealed record MonitorSnapshot(long CheckedAt, long HistoryStartedAt, MonitorService[] Checks,
    MonitorIncident[] Incidents);

// These records describe health checks observed by this API process, not historical application logs.
public sealed class AdminMonitor(HealthCheckService healthChecks, TimeProvider clock)
{
    private static readonly string[] ServiceIds = ["api", "database"];
    private readonly SemaphoreSlim gate = new(1);
    private readonly List<MonitorIncident> incidents = [];
    private readonly long historyStartedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
    private MonitorSnapshot? latest;

    public async Task<MonitorSnapshot> CheckAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (latest is not null && clock.GetUtcNow().ToUnixTimeMilliseconds() - latest.CheckedAt < 2000)
                return latest;
            var report = await healthChecks.CheckHealthAsync(
                registration => ServiceIds.Contains(registration.Name, StringComparer.Ordinal), ct);
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var checks = ServiceIds.Select(id => ReadService(id, report)).ToArray();
            foreach (var check in checks)
            {
                var index = incidents.FindIndex(item => item.Service == check.Id && item.RecoveredAt is null);
                if (check.Status == "healthy" && index >= 0)
                    incidents[index] = incidents[index] with { RecoveredAt = now };
                else if (check.Status == "unavailable" && index < 0)
                    incidents.Add(new(Guid.NewGuid().ToString("N"), check.Id, "健康检查失败", "danger",
                        check.Note, null, now, null));
            }
            var cutoff = now - (long)TimeSpan.FromHours(24).TotalMilliseconds;
            incidents.RemoveAll(item => item.RecoveredAt is { } recoveredAt && recoveredAt < cutoff);
            latest = new(now, historyStartedAt, checks,
                incidents.OrderByDescending(item => item.OccurredAt).ThenBy(item => item.Id).ToArray());
            return latest;
        }
        finally { gate.Release(); }
    }

    private static MonitorService ReadService(string id, HealthReport report)
    {
        var metric = id == "api" ? "本次响应" : "检查耗时";
        if (!report.Entries.TryGetValue(id, out var entry))
            return new(id, "unknown", null, null, metric, "尚未获取有效的健康检查结果。");

        var healthy = entry.Status == HealthStatus.Healthy;
        var note = healthy ? "健康检查通过。" : "健康检查未通过，恢复状态需后续检查确认。";
        if (id == "database" && !healthy)
            note = entry.Description == "Database migrations are pending."
                ? "数据库存在待应用的迁移，就绪检查未通过。" : "数据库连接检查未通过。";
        // API request latency is measured by the caller. The database latency comes from its readiness check.
        double? latency = id == "database" ? Math.Round(entry.Duration.TotalMilliseconds, 1) : null;
        return new(id, healthy ? "healthy" : "unavailable", latency, null, metric, note);
    }
}

public sealed class AdminMonitorPoller(AdminMonitor monitor, IServiceScopeFactory scopes,
    ILogger<AdminMonitorPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var interval = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                var snapshot = await monitor.CheckAsync(stoppingToken);
                if (snapshot.Checks.Any(x => x.Id == "database" && x.Status == "healthy"))
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<AdminNotificationCollector>().ObserveAsync(snapshot, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Core service checks or notification collection could not be completed.");
            }
        } while (await interval.WaitForNextTickAsync(stoppingToken));
    }
}
