using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Membership;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Admin;

public sealed class AdminNotificationCollector(AppDbContext db, TimeProvider clock,
    AdminAlertSignals? signals = null, AdminAlertWatchdog? watchdog = null)
{
    internal static Task LockAsync(AppDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(410122605)", ct);

    internal static void StageAudit(AppDbContext db, string auditId, string description, DateTimeOffset now) =>
        db.AdminNotifications.Add(new()
        {
            Kind = "security", Description = description, AuditId = auditId, OccurredAtUtc = now,
            LastOccurredAtUtc = now, HandledAtUtc = now, Resolution = "recorded"
        });

    internal static async Task StageOrderFailureAsync(AppDbContext db, MembershipOrder order, DateTimeOffset now, CancellationToken ct)
    {
        await LockAsync(db, ct);
        var existing = await db.AdminNotifications.Where(x => x.Kind == "order" && x.OrderId == order.Id && x.HandledAtUtc == null)
            .OrderByDescending(x => x.AlertKey != null).ThenBy(x => x.OccurredAtUtc).FirstOrDefaultAsync(ct);
        UpdateOrder(db, existing, order, now);
    }

    public async Task ObserveAsync(MonitorSnapshot monitor, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize observations so concurrent pollers cannot open two records for the same incident.
        await LockAsync(db, ct);
        var open = await db.AdminNotifications.Where(x => x.HandledAtUtc == null)
            .OrderByDescending(x => x.AlertKey != null).ThenBy(x => x.OccurredAtUtc).ToListAsync(ct);
        var now = clock.GetUtcNow();
        await ObserveOrdersAsync(open, now, ct);
        if (watchdog?.Enabled == true)
        {
            var snapshot = watchdog.ReadForApi();
            if (snapshot is not null) await ImportWatchdogAsync(snapshot, ct);
            CloseObservedHealthy(open.Where(x => x.MonitorIncidentId.HasValue && x.LastOccurredAtUtc == null), monitor);
        }
        else await ObserveMonitorAsync(open, monitor, ct);
        if (signals is not null) await ObserveSignalsAsync(open, signals, now, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task ObserveOrdersAsync(List<AdminNotification> open, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.AddMinutes(-2);
        var exceptions = await AdminOrdersController.PendingOrderExceptions(db.MembershipOrders.AsNoTracking())
            .Where(x => x.ActivationStatus == "failed" || x.PaidAtUtc <= cutoff).ToDictionaryAsync(x => x.Id, ct);
        var openOrderIds = open.Where(x => x.Kind == "order" && x.OrderId.HasValue)
            .Select(x => x.OrderId!.Value).Distinct().ToArray();
        var orderStates = await db.MembershipOrders.AsNoTracking().Where(x => openOrderIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Status, x.ActivationStatus }).ToDictionaryAsync(x => x.Id, ct);
        foreach (var notice in open.Where(x => x.Kind == "order"))
        {
            if (notice.OrderId is { } id && exceptions.Remove(id, out var order))
            {
                UpdateOrder(db, notice, order, now);
                continue;
            }
            var state = notice.OrderId is { } orderId ? orderStates.GetValueOrDefault(orderId) : null;
            notice.HandledAtUtc = now;
            notice.Resolution = state?.Status == "refunded" ? "refunded"
                : state?.ActivationStatus == "activated" ? "fulfilled" : "handled";
        }
        foreach (var order in exceptions.Values) UpdateOrder(db, null, order, now);
    }

    private static void UpdateOrder(AppDbContext db, AdminNotification? existing, MembershipOrder order, DateTimeOffset now)
    {
        var key = $"order:{order.Id:N}";
        var failed = order.ActivationStatus == "failed";
        var orderNumber = $"FT{order.Id:N}".ToUpperInvariant();
        var origin = order.PaymentProvider == "simulation" ? "模拟支付 · " : "";
        var description = failed ? $"{origin}订单 {orderNumber} 会员权益发放失败。"
            : $"{origin}订单 {orderNumber} 已付款超过 2 分钟，会员权益尚未发放。";
        var at = failed ? order.ActivationAttemptedAtUtc ?? now : now;
        if (existing is null)
        {
            db.AdminNotifications.Add(new()
            {
                Kind = "order", OrderId = order.Id, OccurredAtUtc = at, LastOccurredAtUtc = at,
                Description = description, AlertKey = key
            });
            return;
        }
        existing.AlertKey ??= key;
        existing.Description = description;
        if (!failed || at.ToUnixTimeMilliseconds() <= (existing.LastOccurredAtUtc ?? existing.OccurredAtUtc).ToUnixTimeMilliseconds()) return;
        existing.LastOccurredAtUtc = at;
        existing.OccurrenceCount++;
    }

    private async Task ObserveSignalsAsync(List<AdminNotification> open, AdminAlertSignals source, DateTimeOffset now, CancellationToken ct)
    {
        var samples = source.Evaluate(now);
        var loginKeys = samples.Where(x => x.Kind == "login").Select(x => x.Key).ToHashSet();
        if (source.HasFullLoginWindow(now))
            foreach (var notice in open.Where(x => x.AlertKey?.StartsWith(source.LoginPrefix, StringComparison.Ordinal) == true &&
                !loginKeys.Contains(x.AlertKey!)))
                Resolve(notice, now);

        var byKey = open.Where(x => x.AlertKey != null).ToDictionary(x => x.AlertKey!);
        var originalKeys = byKey.Keys.ToHashSet();
        var episodeIds = samples.Where(x => x.EpisodeId.HasValue).Select(x => x.EpisodeId!.Value).ToArray();
        var known = await db.AdminNotifications.Where(x => x.MonitorIncidentId.HasValue && episodeIds.Contains(x.MonitorIncidentId.Value))
            .ToDictionaryAsync(x => x.MonitorIncidentId!.Value, ct);
        var added = new List<AdminNotification>();
        foreach (var sample in samples)
        {
            var notice = byKey.GetValueOrDefault(sample.Key);
            if (sample.EpisodeId is { } episodeId)
            {
                if (!known.TryGetValue(episodeId, out var episode))
                {
                    episode = notice is { HandledAtUtc: null } &&
                        (notice.LastOccurredAtUtc ?? notice.OccurredAtUtc) <= sample.At ? notice : null;
                    if (episode is null)
                    {
                        episode = new AdminNotification
                        {
                            Kind = sample.Kind, Service = sample.Service, AlertKey = sample.Key,
                            Description = sample.Description, OccurredAtUtc = sample.FirstAt ?? sample.At
                        };
                        added.Add(episode);
                    }
                    episode.MonitorIncidentId = episodeId;
                }
                episode.LastOccurredAtUtc = sample.At;
                episode.OccurrenceCount = sample.Count;
                if (sample.RecoveredAt is { } recovered) Resolve(episode, recovered);
                if (!episode.HandledAtUtc.HasValue) byKey[sample.Key] = episode;
                continue;
            }
            if (notice is not null && sample.At.ToUnixTimeMilliseconds() <=
                (notice.LastOccurredAtUtc ?? notice.OccurredAtUtc).ToUnixTimeMilliseconds()) continue;
            if (!sample.Active)
            {
                if (notice is not null) Resolve(notice, sample.At);
                continue;
            }
            if (notice is null || notice.HandledAtUtc.HasValue)
            {
                added.Add(new()
                {
                    Kind = sample.Kind, Service = sample.Service, AlertKey = sample.Key, Description = sample.Description,
                    OccurredAtUtc = sample.At, LastOccurredAtUtc = sample.At, OccurrenceCount = sample.Count
                });
                continue;
            }
            notice.Description = sample.Description;
            notice.LastOccurredAtUtc = sample.At;
            notice.OccurrenceCount = sample.Count;
        }
        if (added.Any(x => originalKeys.Contains(x.AlertKey!))) await db.SaveChangesAsync(ct);
        db.AdminNotifications.AddRange(added);
    }

    private async Task ImportWatchdogAsync(AdminAlertWatchdog.Snapshot snapshot, CancellationToken ct)
    {
        var ids = snapshot.Incidents.Select(x => x.Id).ToArray();
        var existing = await db.AdminNotifications.Where(x => x.MonitorIncidentId.HasValue && ids.Contains(x.MonitorIncidentId.Value))
            .ToDictionaryAsync(x => x.MonitorIncidentId!.Value, ct);
        foreach (var incident in snapshot.Incidents)
        {
            if (!existing.TryGetValue(incident.Id, out var notice))
            {
                notice = new()
                {
                    Id = incident.Id, Kind = "system", Service = incident.Service, MonitorIncidentId = incident.Id,
                    Description = incident.Description, OccurredAtUtc = incident.OccurredAt
                };
                db.AdminNotifications.Add(notice);
            }
            notice.OccurrenceCount = incident.Count;
            notice.LastOccurredAtUtc = incident.LastOccurredAt;
            if (incident.RecoveredAt is { } recovered) Resolve(notice, recovered);
        }
    }

    private static void Resolve(AdminNotification notice, DateTimeOffset at)
    {
        notice.HandledAtUtc ??= at;
        notice.Resolution = "recovered";
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
                    x.OccurredAtUtc <= occurredAt && x.MonitorIncidentId is { } previous && !incidentIds.Contains(previous));
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
            if (recoveredAt.HasValue) Resolve(notice, recoveredAt.Value);
        }

        CloseObservedHealthy(open, monitor);
    }

    private static void CloseObservedHealthy(IEnumerable<AdminNotification> open, MonitorSnapshot monitor)
    {
        var checkedAt = DateTimeOffset.FromUnixTimeMilliseconds(monitor.CheckedAt);
        foreach (var notice in open.Where(x => x.Kind == "system" && x.HandledAtUtc == null && x.OccurredAtUtc <= checkedAt))
        {
            if (monitor.Checks.Any(x => x.Id == notice.Service && x.Status == "healthy"))
                Resolve(notice, checkedAt);
        }
    }
}
