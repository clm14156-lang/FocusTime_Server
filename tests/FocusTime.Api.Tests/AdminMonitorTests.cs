using System.Net;
using FocusTime.Api.Modules.Admin;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FocusTime.Api.Tests;

public sealed class AdminMonitorTests
{
    [Fact]
    public async Task MissingChecks_AreUnknown_AndNoMetricsOrIncidentsAreInvented()
    {
        var health = new ControlledHealthChecks();
        var clock = new TestClock();
        var monitor = new AdminMonitor(health, clock);
        var snapshot = await monitor.CheckAsync(default);
        Assert.Equal(new[] { "api", "database" }, snapshot.Checks.Select(check => check.Id).ToArray());
        Assert.All(snapshot.Checks, check => Assert.Equal("unknown", check.Status));
        Assert.All(snapshot.Checks, check => Assert.Null(check.ErrorRate));
        Assert.Empty(snapshot.Incidents);
    }

    [Fact]
    public async Task ActualCheckTransitions_CreateOneAlert_RequireHealthyRecovery_AndRetainHistory()
    {
        var health = new ControlledHealthChecks();
        var clock = new TestClock();
        var monitor = new AdminMonitor(health, clock);
        health.Statuses["api"] = health.Statuses["database"] = HealthStatus.Healthy;
        health.Statuses["sync"] = HealthStatus.Unhealthy;
        var initial = await monitor.CheckAsync(default);
        Assert.Equal(2, initial.Checks.Length);
        Assert.All(initial.Checks, check => Assert.Equal("healthy", check.Status));
        Assert.Equal(12, initial.Checks.Single(check => check.Id == "database").LatencyMs);
        Assert.Empty(initial.Incidents);

        clock.Advance(TimeSpan.FromSeconds(3));
        health.Statuses["database"] = HealthStatus.Unhealthy;
        var failure = await monitor.CheckAsync(default);
        var incident = Assert.Single(failure.Incidents);
        Assert.Null(incident.RecoveredAt);
        Assert.Null(incident.Impact);
        Assert.Equal("database", incident.Service);

        clock.Advance(TimeSpan.FromSeconds(3));
        var repeated = await monitor.CheckAsync(default);
        Assert.Equal(incident, Assert.Single(repeated.Incidents));

        clock.Advance(TimeSpan.FromSeconds(3));
        health.Statuses.Remove("database");
        var unknown = await monitor.CheckAsync(default);
        Assert.Equal("unknown", unknown.Checks.Single(check => check.Id == "database").Status);
        Assert.Null(Assert.Single(unknown.Incidents).RecoveredAt);

        clock.Advance(TimeSpan.FromSeconds(3));
        health.Statuses["api"] = HealthStatus.Unhealthy;
        Assert.Equal(2, (await monitor.CheckAsync(default)).Incidents.Count(item => item.RecoveredAt is null));

        clock.Advance(TimeSpan.FromSeconds(3));
        health.Statuses["database"] = HealthStatus.Healthy;
        health.Statuses["api"] = HealthStatus.Healthy;
        var recovered = await monitor.CheckAsync(default);
        Assert.Equal(2, recovered.Incidents.Length);
        Assert.All(recovered.Incidents, item => Assert.NotNull(item.RecoveredAt));
        Assert.Equal(incident.OccurredAt, recovered.Incidents.Single(item => item.Id == incident.Id).OccurredAt);
        clock.Advance(TimeSpan.FromHours(25));
        Assert.Empty((await monitor.CheckAsync(default)).Incidents);
    }

    [Fact]
    public async Task UnauthenticatedMonitoring_DoesNotExposeData()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/monitor")).StatusCode);
    }

    private sealed class ControlledHealthChecks : HealthCheckService
    {
        public Dictionary<string, HealthStatus> Statuses { get; } = [];
        public override Task<HealthReport> CheckHealthAsync(Func<HealthCheckRegistration, bool>? predicate,
            CancellationToken cancellationToken = default)
        {
            var entries = Statuses.ToDictionary(pair => pair.Key,
                pair => new HealthReportEntry(pair.Value, null, TimeSpan.FromMilliseconds(12), null, null));
            return Task.FromResult(new HealthReport(entries, TimeSpan.FromMilliseconds(12)));
        }
    }
}
