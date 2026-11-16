using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using System.Text.Json.Serialization;
using FocusTime.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Admin;

public sealed record AdminNotificationItem(Guid Id, string Kind, Guid? OrderId, string? Service,
    string Description, DateTimeOffset OccurredAtUtc, DateTimeOffset? HandledAtUtc, DateTimeOffset? ReadAtUtc,
    DateTimeOffset? LastOccurredAtUtc, int OccurrenceCount, string? Resolution, string? AuditId);

public sealed record AdminNotificationsPage(IReadOnlyList<AdminNotificationItem> Items, int Total, int UnreadCount,
    int Page, int PageSize,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AdminNotificationItem>? LatestItems = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AdminNotificationItem? SelectedItem = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AdminNotificationItem>? LatestUnreadItems = null);

public sealed class AdminNotificationsQuery
{
    [Range(1, 1000000)] public int Page { get; init; } = 1;
    [Range(1, 100)] public int? PageSize { get; init; }
    public bool UnreadOnly { get; init; }
    public bool IncludeLatest { get; init; }
    public bool IncludeLatestUnread { get; init; }
    public Guid? NoticeId { get; init; }
}

[ApiController, Authorize(Policy = AdminAccess.Policy), Route("api/v1/admin/notifications"),
    ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminNotificationsController(AppDbContext db, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public Task<AdminNotificationsPage> List([FromQuery] AdminNotificationsQuery query, CancellationToken ct) =>
        ListAsync(AdministratorId, query, 20, ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AdminNotificationItem>> Detail(Guid id, CancellationToken ct)
    {
        var notice = await db.AdminNotifications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (notice is null) return NotFound();
        var read = await db.AdminNotificationReads.Where(x => x.AdministratorId == AdministratorId && x.NotificationId == id)
            .Select(x => (DateTimeOffset?)x.ReadAtUtc).SingleOrDefaultAsync(ct);
        return new AdminNotificationItem(notice.Id, notice.Kind, notice.OrderId, notice.Service, notice.Description,
            notice.OccurredAtUtc, notice.HandledAtUtc, read, notice.LastOccurredAtUtc, notice.OccurrenceCount,
            notice.Resolution, notice.AuditId);
    }

    [HttpPost("{id:guid}/read")]
    public async Task<ActionResult<AdminNotificationsPage>> Read(Guid id, [FromQuery] AdminNotificationsQuery query, CancellationToken ct)
    {
        if (Request.Headers["X-FocusTime-Admin"] != "1") return StatusCode(403);
        if (!await db.AdminNotifications.AnyAsync(x => x.Id == id, ct)) return NotFound();

        var administratorId = AdministratorId;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO admin.notification_reads ("AdministratorId", "NotificationId", "ReadAtUtc")
            VALUES ({administratorId}, {id}, {clock.GetUtcNow()})
            ON CONFLICT ("AdministratorId", "NotificationId") DO NOTHING
            """, ct);
        return await ListAsync(administratorId, query, 3, ct);
    }

    [HttpPost("read-all")]
    public async Task<ActionResult<AdminNotificationsPage>> ReadAll([FromQuery] AdminNotificationsQuery query, CancellationToken ct)
    {
        if (Request.Headers["X-FocusTime-Admin"] != "1") return StatusCode(403);
        var administratorId = AdministratorId;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO admin.notification_reads ("AdministratorId", "NotificationId", "ReadAtUtc")
            SELECT {administratorId}, notice."Id", {clock.GetUtcNow()}
            FROM admin.notifications notice
            WHERE NOT EXISTS (
                SELECT 1 FROM admin.notification_reads receipt
                WHERE receipt."AdministratorId" = {administratorId} AND receipt."NotificationId" = notice."Id"
            )
            ORDER BY notice."Id"
            ON CONFLICT ("AdministratorId", "NotificationId") DO NOTHING
            """, ct);
        return await ListAsync(administratorId, query, 3, ct);
    }

    // The administrator policy validates the identifier before dispatching to this controller.
    private Guid AdministratorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<AdminNotificationsPage> ListAsync(Guid administratorId, AdminNotificationsQuery query,
        int defaultPageSize, CancellationToken ct)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var reads = db.AdminNotificationReads.Where(x => x.AdministratorId == administratorId);
        var all = from notice in db.AdminNotifications.AsNoTracking()
            join receipt in reads on notice.Id equals receipt.NotificationId into receipts
            from receipt in receipts.DefaultIfEmpty()
            select new NotificationReadRow { Notice = notice, ReadAtUtc = (DateTimeOffset?)receipt.ReadAtUtc };
        var unread = all.Where(x => x.ReadAtUtc == null);
        var total = await db.AdminNotifications.CountAsync(ct);
        var unreadCount = await unread.CountAsync(ct);
        var filteredCount = query.UnreadOnly ? unreadCount : total;
        var pageSize = query.PageSize ?? defaultPageSize;
        var page = Math.Min(query.Page, Math.Max(1, (int)Math.Ceiling((double)filteredCount / pageSize)));
        var items = await Project(Sort(query.UnreadOnly ? unread : all).Skip((page - 1) * pageSize).Take(pageSize)).ToArrayAsync(ct);
        AdminNotificationItem[]? latest = null;
        if (query.IncludeLatest && (query.UnreadOnly || page != 1 || pageSize < 3))
            latest = await Project(Sort(all).Take(3)).ToArrayAsync(ct);
        AdminNotificationItem[]? latestUnread = null;
        if (query.IncludeLatestUnread && unreadCount > 0)
        {
            const int previewSize = 4;
            var unreadOnFirstPage = page == 1 ? items.Count(x => x.ReadAtUtc is null) : 0;
            if (unreadOnFirstPage < Math.Min(previewSize, unreadCount))
                latestUnread = await Project(Sort(unread).Take(previewSize)).ToArrayAsync(ct);
        }

        AdminNotificationItem? selected = null;
        if (query.NoticeId is { } id)
            selected = items.FirstOrDefault(x => x.Id == id) ?? latest?.FirstOrDefault(x => x.Id == id)
                ?? latestUnread?.FirstOrDefault(x => x.Id == id)
                ?? await Project(all.Where(x => x.Notice.Id == id)).SingleOrDefaultAsync(ct);
        await snapshot.CommitAsync(ct);
        return new(items, total, unreadCount, page, pageSize, latest, selected, latestUnread);
    }

    private static IOrderedQueryable<NotificationReadRow> Sort(IQueryable<NotificationReadRow> query) =>
        query.OrderByDescending(x => x.Notice.OccurredAtUtc).ThenByDescending(x => x.Notice.Id);

    private static IQueryable<AdminNotificationItem> Project(IQueryable<NotificationReadRow> query) =>
        query.Select(x => new AdminNotificationItem(x.Notice.Id, x.Notice.Kind, x.Notice.OrderId, x.Notice.Service,
            x.Notice.Description, x.Notice.OccurredAtUtc, x.Notice.HandledAtUtc, x.ReadAtUtc, x.Notice.LastOccurredAtUtc,
            x.Notice.OccurrenceCount, x.Notice.Resolution, x.Notice.AuditId));

    private sealed class NotificationReadRow
    {
        public required AdminNotification Notice { get; init; }
        public DateTimeOffset? ReadAtUtc { get; init; }
    }
}
