using System.ComponentModel.DataAnnotations;
using System.Data;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Membership;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Admin;

public sealed record AdminUserSecurity(string? Device, string? OperatingSystem, string? ClientVersion,
    string? Ip, string? Region, bool PasswordSet, DateTimeOffset? PasswordResetAtUtc, string? Risk);
public sealed record AdminUserCloudStats(long FocusSeconds, long CompletedTasks, int FocusDays, int? Goals,
    long Records, int Rules, int? RemainingTasks, int? TotalTasks, DateTimeOffset? LastSyncAtUtc);
public sealed record AdminUserAccount(Guid UserId, AdminUserSecurity Security, AdminUserCloudStats Stats);
public sealed record AdminUserCurrentMembership(Guid GrantId, string Plan, DateTimeOffset StartsAtUtc,
    DateTimeOffset? ExpiresAtUtc, string? Source, Guid? OrderId, string? OrderNumber, bool IsTrial = false);
public sealed record AdminUserMembership(Guid UserId, AdminUserCurrentMembership? Current);
public sealed record AdminUserSession(Guid Id, Guid DeviceId, string Name, string OperatingSystem,
    string ClientVersion, DateTimeOffset FirstLoginAtUtc, DateTimeOffset LastActiveAtUtc,
    DateTimeOffset ExpiresAtUtc, string Status, string? Ip, string? Location);
public sealed record AdminUserSessions(Guid UserId, IReadOnlyList<AdminUserSession> Items);
public sealed class AdminUserAuditQuery
{
    [Range(1, 1000000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 5;
}
public sealed record AdminUserAuditRow(string Id, DateTimeOffset Time, string Admin, string Type,
    string Content, string Result, string? Note);
public sealed record AdminUserAuditPage(Guid UserId, IReadOnlyList<AdminUserAuditRow> Items,
    int Total, int Page, int PageSize);
public sealed record AdminUserCloudTotals(long FocusSeconds, long Records, int FocusDays, long CompletedTasks);

[ApiController, Authorize(Policy = AdminAccess.Policy), Route("api/v1/admin/users/{userId:guid}"),
    ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminUserDetailsController(AppDbContext db, TimeProvider clock) : ControllerBase
{
    private async Task<AppUser> FindUser(Guid id, CancellationToken ct) =>
        await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AuthFault(404, "user_not_found", "User not found.");

    [HttpGet("account")]
    public async Task<AdminUserAccount> Account(Guid userId, CancellationToken ct)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var user = await FindUser(userId, ct);
        var latest = await (from session in db.LoginSessions.AsNoTracking()
            join device in db.LoginDevices.AsNoTracking() on session.DeviceId equals device.Id
            where session.UserId == userId && device.UserId == userId
            orderby session.CreatedAtUtc descending, session.Id
            select new { device.Name, device.OperatingSystem, device.ClientVersion }).FirstOrDefaultAsync(ct);
        // Aggregate current, non-deleted server snapshots. Never read client-local tasks or targets.
        // Completed tasks are distinct task IDs in the retained completed-task snapshots.
        var totals = await db.Database.SqlQuery<AdminUserCloudTotals>($"""
            SELECT COALESCE(SUM((r."PayloadJson"->>'actualSeconds')::bigint), 0)::bigint AS "FocusSeconds",
                COUNT(*)::bigint AS "Records",
                COUNT(DISTINCT (timezone('Asia/Shanghai', (r."PayloadJson"->>'completedAtUtc')::timestamptz))::date)
                    FILTER (WHERE (r."PayloadJson"->>'actualSeconds')::int > 0)::int AS "FocusDays",
                (SELECT COUNT(DISTINCT task->>'taskId') FROM focus.records task_record
                    CROSS JOIN LATERAL jsonb_array_elements(task_record."PayloadJson"->'completedTasks') task
                    WHERE task_record."UserId" = {userId} AND NOT task_record."IsDeleted")::bigint AS "CompletedTasks"
            FROM focus.records r WHERE r."UserId" = {userId} AND NOT r."IsDeleted"
            """).SingleAsync(ct);
        var rules = await db.Database.SqlQuery<int>($"""
            SELECT jsonb_array_length(rules) AS "Value" FROM focus.website_rule_sets WHERE user_id = {userId}
            """).SingleOrDefaultAsync(ct);
        // A successful push receipt is observable; a pull or a website-rule sync has no stored timestamp.
        var synced = await db.FocusSyncReceipts.AsNoTracking().Where(x => x.UserId == userId &&
            (x.Status == "Applied" || x.Status == "Duplicate")).MaxAsync(x => (DateTimeOffset?)x.ReceivedAtUtc, ct);
        var result = new AdminUserAccount(userId,
            new(latest?.Name, latest?.OperatingSystem, latest?.ClientVersion, null, null,
                !string.IsNullOrEmpty(user.PasswordHash), null, null),
            new(totals.FocusSeconds, totals.CompletedTasks, totals.FocusDays, null,
                totals.Records, rules, null, null, synced));
        await snapshot.CommitAsync(ct);
        return result;
    }

    [HttpGet("membership")]
    public async Task<AdminUserMembership> Membership(Guid userId, CancellationToken ct)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await FindUser(userId, ct);
        var now = clock.GetUtcNow();
        // Same selection and precedence as MembershipService.GetAsync and AdminUsersController.
        var grant = await db.MembershipGrants.AsNoTracking().Where(x => x.UserId == userId && x.RevokedAtUtc == null &&
            x.StartsAtUtc <= now && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now))
            .OrderByDescending(x => x.Kind == "lifetime").ThenByDescending(x => x.ExpiresAtUtc)
            .ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
        AdminUserCurrentMembership? current = null;
        if (grant is not null)
        {
            var order = await db.MembershipOrders.AsNoTracking().Where(x => x.UserId == userId &&
                x.MembershipGrantId == grant.Id && (x.Status == "paid" || x.Status == "refunding" || x.Status == "refunded") && x.ActivationStatus == "activated")
                .OrderBy(x => x.ActivatedAtUtc).ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
            var isTrial = grant.Id == userId && grant.ExpiresAtUtc.HasValue &&
                await MembershipService.RegistrationTrialAudits(db).AnyAsync(x => x.UserId == userId, ct);
            var manual = order is null && !isTrial && await db.MembershipAudits.AsNoTracking().AnyAsync(x => x.UserId == userId &&
                x.GrantId == grant.Id && x.Action == "grant" && x.Actor != "membership-order", ct);
            current = new(grant.Id, grant.Kind, grant.StartsAtUtc, grant.ExpiresAtUtc,
                isTrial ? "registration-trial" : order is not null ? "order" : manual ? "manual" : null,
                order?.Id, order is null ? null : MembershipOrderService.ToResponse(order, now).OrderNumber, isTrial);
        }
        await snapshot.CommitAsync(ct);
        return new(userId, current);
    }

    [HttpGet("sessions")]
    public async Task<AdminUserSessions> Sessions(Guid userId, CancellationToken ct)
    {
        var user = await FindUser(userId, ct);
        var now = clock.GetUtcNow();
        var rows = await (from session in db.LoginSessions.AsNoTracking()
            join device in db.LoginDevices.AsNoTracking() on session.DeviceId equals device.Id
            where session.UserId == userId && device.UserId == userId
            orderby session.LastSeenAtUtc descending, session.Id
            select new AdminUserSession(session.Id, device.Id, device.Name, device.OperatingSystem,
                device.ClientVersion, session.CreatedAtUtc, session.LastSeenAtUtc, session.ExpiresAtUtc,
                session.RevokedAtUtc != null || session.SecurityStamp != user.SecurityStamp ||
                    (user.LockoutEnabled && user.LockoutEnd == DateTimeOffset.MaxValue) ? "revoked" :
                    session.ExpiresAtUtc <= now ? "expired" : session.AccessExpiresAtUtc <= now ? "refresh-required" : "valid",
                null, null)).ToListAsync(ct);
        return new(userId, rows);
    }

    [HttpGet("audit")]
    public async Task<AdminUserAuditPage> Audit(Guid userId, [FromQuery] AdminUserAuditQuery filters, CancellationToken ct)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await FindUser(userId, ct);
        // A security audit currently has no target-user column: only its explicitly self-directed
        // password operation can be associated with ActorUserId. Never treat an actor as a target otherwise.
        // Payment automation is excluded; CLI membership audits and admin-prefixed order events are retained.
        var query = db.Database.SqlQuery<AdminUserAuditRow>($"""
            SELECT 'membership:' || a."OperationId"::text AS "Id", a."CreatedAtUtc" AS "Time",
                a."Actor" AS "Admin", 'membership.' || a."Action" AS "Type",
                CASE WHEN a."Action" = 'revoke' THEN '撤销会员授权' ELSE '授予会员资格' END AS "Content",
                'success' AS "Result", a."Reason" AS "Note"
            FROM membership.audit a WHERE a."UserId" = {userId} AND a."Actor" <> 'membership-order'
                AND a."Actor" NOT LIKE 'admin-adjustment:%'
            UNION ALL
            SELECT 'adjustment:' || a."OperationId"::text, a."AppliedAtUtc", 'admin:' || a."ActorUserId"::text,
                'membership.' || a."Action",
                CASE WHEN a."Action" = 'grant' THEN '管理员发放会员（' || CASE a."Plan" WHEN 'monthly' THEN '月会员' WHEN 'annual' THEN '年会员' ELSE '永久会员' END || '）'
                    WHEN a."Scope" = 'admin-gifts' THEN '撤销管理员赠送的会员权益' ELSE '撤销全部会员权益' END,
                'success', CASE a."ReasonCode"
                    WHEN 'compensation' THEN '用户补偿' WHEN 'promotion' THEN '活动赠送' WHEN 'trial' THEN '免费体验'
                    WHEN 'internal-test' THEN '内部测试' WHEN 'issued-in-error' THEN '发放错误' WHEN 'ineligible' THEN '赠送资格不符'
                    WHEN 'test-ended' THEN '内部测试结束' WHEN 'refund-request' THEN '用户申请退款' ELSE '其他原因' END
                    || COALESCE('：' || a."Note", '')
            FROM membership.admin_adjustments a WHERE a."UserId" = {userId} AND a."State" = 'applied'
            UNION ALL
            SELECT 'security:' || a."Id"::text, a."OccurredAtUtc", 'admin:' || a."ActorUserId"::text,
                a."Operation", '修改管理员自身密码', a."Result", a."FailureCode"
            FROM identity.admin_security_audit a WHERE a."ActorUserId" = {userId} AND a."Operation" = 'admin.password.change'
            UNION ALL
            SELECT 'order:' || e."Id"::text, e."OccurredAtUtc", e."Actor", 'membership.retry-activation',
                CASE WHEN e."Kind" = 'membership_activated' THEN '重试开通订单会员权益成功' ELSE '重试开通订单会员权益失败' END,
                CASE WHEN e."Kind" = 'membership_activated' THEN 'success' ELSE 'failure' END, o."Id"::text
            FROM membership.order_events e JOIN membership.orders o ON o."Id" = e."OrderId"
            WHERE o."UserId" = {userId} AND e."Actor" LIKE 'admin:%'
                AND e."Kind" IN ('membership_activated', 'membership_activation_failed')
            UNION ALL
            SELECT 'operation:' || a."Id"::text, a."OccurredAtUtc", a."Administrator",
                CASE a."Action" WHEN {AdminUserActionsController.DisableAction} THEN 'disable'
                    WHEN {AdminUserActionsController.RestoreAction} THEN 'restore' ELSE 'sign-out' END,
                a."Action", a."Status", a."FailureCode"
            FROM admin.operation_logs a
            WHERE a."Category" = 'users' AND a."TargetId" = {userId.ToString()}
                AND a."Action" IN ({AdminUserActionsController.DisableAction}, {AdminUserActionsController.RestoreAction}, {AdminUserActionsController.SignOutAction})
            """);
        var total = await query.CountAsync(ct);
        var page = Math.Min(filters.Page, Math.Max(1, (int)Math.Ceiling((double)total / filters.PageSize)));
        var rows = await query.OrderByDescending(x => x.Time).ThenBy(x => x.Id)
            .Skip((page - 1) * filters.PageSize).Take(filters.PageSize).ToListAsync(ct);
        static string ActorKey(string value) => value.StartsWith("admin-refund:", StringComparison.Ordinal) ? value[13..]
            : value.StartsWith("admin:", StringComparison.Ordinal) ? value[6..] : value;
        var actorIds = rows.Select(x => ActorKey(x.Admin))
            .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty).Where(x => x != Guid.Empty).Distinct().ToArray();
        var names = await db.Users.AsNoTracking().Where(x => actorIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Email!, ct);
        var items = rows.Select(row => {
            var actor = ActorKey(row.Admin);
            return row with { Admin = Guid.TryParse(actor, out var id) && names.TryGetValue(id, out var name) ? name : row.Admin };
        }).ToArray();
        await snapshot.CommitAsync(ct);
        return new(userId, items, total, page, filters.PageSize);
    }
}
