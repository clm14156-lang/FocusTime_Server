using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using FocusTime.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Admin;

public sealed record AdminNotificationItem(Guid Id, string Kind, Guid? OrderId, string? Service,
    string Description, DateTimeOffset OccurredAtUtc, DateTimeOffset? HandledAtUtc, DateTimeOffset? ReadAtUtc);

public sealed record AdminNotificationsPage(IReadOnlyList<AdminNotificationItem> Items, int Total, int UnreadCount,
    int Page, int PageSize);

[ApiController, Authorize(Policy = AdminAccess.Policy), Route("api/v1/admin/notifications"),
    ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminNotificationsController(AppDbContext db, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public Task<AdminNotificationsPage> List(CancellationToken ct, bool unreadOnly = false,
        [Range(1, 1000000)] int page = 1, [Range(1, 100)] int pageSize = 20) =>
        ListAsync(AdministratorId, unreadOnly, page, pageSize, ct);

    [HttpPost("{id:guid}/read")]
    public async Task<ActionResult<AdminNotificationsPage>> Read(Guid id, CancellationToken ct)
    {
        if (Request.Headers["X-FocusTime-Admin"] != "1") return StatusCode(403);
        if (!await db.AdminNotifications.AnyAsync(x => x.Id == id, ct)) return NotFound();

        var administratorId = AdministratorId;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO admin.notification_reads ("AdministratorId", "NotificationId", "ReadAtUtc")
            VALUES ({administratorId}, {id}, {clock.GetUtcNow()})
            ON CONFLICT ("AdministratorId", "NotificationId") DO NOTHING
            """, ct);
        return await ListAsync(administratorId, false, 1, 3, ct);
    }

    [HttpPost("read-all")]
    public async Task<ActionResult<AdminNotificationsPage>> ReadAll(CancellationToken ct)
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
        return await ListAsync(administratorId, false, 1, 3, ct);
    }

    // The administrator policy validates the identifier before dispatching to this controller.
    private Guid AdministratorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<AdminNotificationsPage> ListAsync(Guid administratorId, bool unreadOnly,
        int page, int pageSize, CancellationToken ct)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var reads = db.AdminNotificationReads.Where(x => x.AdministratorId == administratorId);
        var all = from notice in db.AdminNotifications.AsNoTracking()
            join receipt in reads on notice.Id equals receipt.NotificationId into receipts
            from receipt in receipts.DefaultIfEmpty()
            select new { Notice = notice, ReadAtUtc = (DateTimeOffset?)receipt.ReadAtUtc };
        var unread = all.Where(x => x.ReadAtUtc == null);
        var total = await db.AdminNotifications.CountAsync(ct);
        var unreadCount = await unread.CountAsync(ct);
        var filteredCount = unreadOnly ? unreadCount : total;
        page = Math.Min(page, Math.Max(1, (int)Math.Ceiling((double)filteredCount / pageSize)));
        var items = await (unreadOnly ? unread : all).OrderByDescending(x => x.Notice.OccurredAtUtc)
            .ThenByDescending(x => x.Notice.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new AdminNotificationItem(x.Notice.Id, x.Notice.Kind, x.Notice.OrderId, x.Notice.Service,
                x.Notice.Description, x.Notice.OccurredAtUtc, x.Notice.HandledAtUtc, x.ReadAtUtc)).ToArrayAsync(ct);
        await snapshot.CommitAsync(ct);
        return new(items, total, unreadCount, page, pageSize);
    }
}
