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
    private static readonly string[] ServiceIds = ["api", "database", "account", "membership", "sync"];
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
        if (!report.Entries.TryGetValue(id, out var entry))
            return id == "api"
                ? new(id, "healthy", null, null, "本次响应", "API 进程存活检查通过，外部访问仍需监控请求确认。")
                : new(id, "unknown", null, null, id == "database" ? "检查耗时" : "响应", "尚未配置独立健康检查。");

        var healthy = entry.Status == HealthStatus.Healthy;
        var note = healthy ? "健康检查通过。" : "健康检查未通过，恢复状态需后续检查确认。";
        if (id == "database" && !healthy)
            note = entry.Description == "Database migrations are pending."
                ? "数据库存在待应用的迁移，就绪检查未通过。" : "数据库连接检查未通过。";
        return new(id, healthy ? "healthy" : "unavailable", Math.Round(entry.Duration.TotalMilliseconds, 1),
            null, "检查耗时", note);
    }
}

public sealed class AdminMonitorPoller(AdminMonitor monitor, ILogger<AdminMonitorPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var interval = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try { await monitor.CheckAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                logger.LogWarning("Core service health checks could not be completed.");
            }
        } while (await interval.WaitForNextTickAsync(stoppingToken));
    }
}
