using FocusTime.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Admin;

public sealed class AdminNotificationCollector(AppDbContext db, TimeProvider clock)
{
    public async Task ObserveAsync(MonitorSnapshot monitor, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize observations so concurrent pollers cannot open two records for the same incident.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(410122605)", ct);
        var open = await db.AdminNotifications.Where(x => x.HandledAtUtc == null).ToListAsync(ct);
        var now = clock.GetUtcNow();
        await ObserveOrdersAsync(open, now, ct);
        await ObserveMonitorAsync(open, monitor, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task ObserveOrdersAsync(List<AdminNotification> open, DateTimeOffset now, CancellationToken ct)
    {
        var exceptions = (await AdminOrdersController.PendingOrderExceptions(db.MembershipOrders.AsNoTracking())
            .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        foreach (var notice in open.Where(x => x.Kind == "order"))
        {
            if (!exceptions.Remove(notice.OrderId!.Value)) notice.HandledAtUtc = now;
        }
        foreach (var id in exceptions)
        {
            var orderNumber = $"FT{id:N}".ToUpperInvariant();
            db.AdminNotifications.Add(new()
            {
                Kind = "order", OrderId = id, OccurredAtUtc = now,
                Description = $"订单 {orderNumber} 已支付，会员权益尚未发放。",
            });
        }
    }

    private async Task ObserveMonitorAsync(List<AdminNotification> open, MonitorSnapshot monitor, CancellationToken ct)
    {
        var incidentIds = monitor.Incidents.Select(x => Guid.Parse(x.Id)).ToHashSet();
        var known = await db.AdminNotifications
            .Where(x => x.MonitorIncidentId.HasValue && incidentIds.Contains(x.MonitorIncidentId.Value))
            .ToDictionaryAsync(x => x.MonitorIncidentId!.Value, ct);
        foreach (var incident in monitor.Incidents.OrderBy(x => x.OccurredAt))
        {
            var incidentId = Guid.Parse(incident.Id);
            var occurredAt = DateTimeOffset.FromUnixTimeMilliseconds(incident.OccurredAt);
            var recoveredAt = incident.RecoveredAt is { } recovery
                ? DateTimeOffset.FromUnixTimeMilliseconds(recovery) : (DateTimeOffset?)null;
            if (!known.TryGetValue(incidentId, out var notice))
            {
                // Continue an open incident from before a process restart.
                notice = open.Find(x => x.Service == incident.Service && x.HandledAtUtc == null &&
                    x.OccurredAtUtc <= occurredAt && !incidentIds.Contains(x.MonitorIncidentId!.Value));
                if (notice is null)
                {
                    notice = new()
                    {
                        Kind = "system", Service = incident.Service, OccurredAtUtc = occurredAt,
                        Description = incident.Service == "database"
                            ? $"数据库健康检查失败。{incident.Error}" : $"API 服务健康检查失败。{incident.Error}",
                    };
                    db.AdminNotifications.Add(notice);
                }
                notice.MonitorIncidentId = incidentId;
            }
            if (recoveredAt.HasValue) notice.HandledAtUtc ??= recoveredAt;
        }

        var checkedAt = DateTimeOffset.FromUnixTimeMilliseconds(monitor.CheckedAt);
        foreach (var notice in open.Where(x => x.Kind == "system" && x.HandledAtUtc == null && x.OccurredAtUtc <= checkedAt))
        {
            if (monitor.Checks.Any(x => x.Id == notice.Service && x.Status == "healthy"))
                notice.HandledAtUtc = checkedAt;
        }
    }
}
