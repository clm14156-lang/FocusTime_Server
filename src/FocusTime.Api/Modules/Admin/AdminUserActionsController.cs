using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Sessions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FocusTime.Api.Modules.Admin;

public sealed record AdminUserActionRequest(Guid OperationId);
public sealed record AdminUserActionResult(Guid UserId);

[ApiController, Authorize(Policy = AdminAccess.Policy), Route("api/v1/admin/users/{userId:guid}"),
    ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminUserActionsController(AppDbContext db, AdminAccess admins, TimeProvider clock) : ControllerBase
{
    public const string DisableAction = "禁用账号";
    public const string RestoreAction = "恢复账号";
    public const string SignOutAction = "强制退出所有设备";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [HttpPost("disable")]
    public Task<AdminUserActionResult> Disable(Guid userId, AdminUserActionRequest request, CancellationToken ct) =>
        Apply(userId, request.OperationId, DisableAction, ct);

    [HttpPost("restore")]
    public Task<AdminUserActionResult> Restore(Guid userId, AdminUserActionRequest request, CancellationToken ct) =>
        Apply(userId, request.OperationId, RestoreAction, ct);

    [HttpPost("sessions/revoke-all")]
    public Task<AdminUserActionResult> SignOut(Guid userId, AdminUserActionRequest request, CancellationToken ct) =>
        Apply(userId, request.OperationId, SignOutAction, ct);

    private async Task<AdminUserActionResult> Apply(Guid userId, Guid operationId, string action, CancellationToken ct)
    {
        if (Request.Headers["X-FocusTime-Admin"] != "1")
            throw new AuthFault(403, "admin_header_required", "Administrator mutation header required.");
        if (operationId == Guid.Empty)
            throw new AuthFault(400, "invalid_operation_id", "Operation ID is required.");
        var actorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.Length == 0) authorization = "Bearer " + Request.Cookies[AdminAccess.CookieName];

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var emails = await db.Users.AsNoTracking().Where(user => user.Id == actorId || user.Id == userId)
            .ToDictionaryAsync(user => user.Id, user => user.Email!, ct);
        if (!emails.TryGetValue(actorId, out var actorEmail))
            throw new AuthFault(401, "session_expired", "Please sign in again.");
        if (!emails.TryGetValue(userId, out var targetEmail))
            throw new AuthFault(404, "user_not_found", "User not found.");
        foreach (var email in new[] { actorEmail, targetEmail }.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
            await AccountTransactions.LockAsync(db, email, ct);
        if (await db.Users.CountAsync(user => (user.Id == actorId && user.Email == actorEmail) ||
            (user.Id == userId && user.Email == targetEmail), ct) != emails.Count)
            throw new AuthFault(409, "account_changed", "The account changed. Please refresh and try again.");
        await AccountTransactions.LockSessionAsync(db, clock, User, authorization, ct);

        var targetId = userId.ToString();
        var previous = await db.AdminOperationLogs.AsNoTracking().SingleOrDefaultAsync(log => log.Id == operationId, ct);
        if (previous is not null)
        {
            if (previous.ActorUserId != actorId || previous.TargetId != targetId || previous.Action != action || previous.Status != "success")
                throw OperationConflict();
            await transaction.CommitAsync(ct);
            return new(userId);
        }

        var user = await db.Users.SingleAsync(user => user.Id == userId, ct);
        if (user.ClosedAtUtc is not null)
            throw new AuthFault(409, "user_closed", "This account has been closed.");
        if (action == DisableAction && admins.Allows(userId))
            throw new AuthFault(409, "administrator_protected", "Administrator accounts cannot be disabled here.");

        var now = clock.GetUtcNow();
        var disabled = user.LockoutEnabled && user.LockoutEnd == DateTimeOffset.MaxValue;
        var changes = new List<AdminAuditChange>();
        if (action == DisableAction && !disabled)
        {
            var before = user.LockoutEnabled && user.LockoutEnd > now ? "暂时锁定" : "正常";
            user.LockoutEnabled = true;
            user.LockoutEnd = DateTimeOffset.MaxValue;
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
            changes.Add(new("账号状态", before, "已禁用"));
        }
        if (action == RestoreAction && disabled)
        {
            user.LockoutEnd = null;
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
            changes.Add(new("账号状态", "已禁用", "正常"));
        }
        if (action == DisableAction || action == SignOutAction || (action == RestoreAction && disabled))
        {
            var revoked = await db.LoginSessions.Where(session => session.UserId == userId && session.RevokedAtUtc == null && session.ExpiresAtUtc > now)
                .ExecuteUpdateAsync(update => update.SetProperty(session => session.RevokedAtUtc, now), ct);
            if (revoked > 0) changes.Add(new("退出会话数", null, revoked.ToString(CultureInfo.InvariantCulture)));
        }

        db.AdminOperationLogs.Add(new() { Id = operationId, ActorUserId = actorId, Administrator = actorEmail,
            OccurredAtUtc = now, Category = "users", Action = action, TargetId = targetId, Target = user.Email!,
            Source = "后台 · 用户管理", Ip = HttpContext.Connection.RemoteIpAddress?.ToString(), Status = "success",
            ChangesJson = JsonSerializer.Serialize(changes, Json) });
        if (action == DisableAction && !disabled)
            AdminNotificationCollector.StageAudit(db, $"operation:{operationId}", "管理员已禁用用户账号。", now);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw OperationConflict(); }
        await transaction.CommitAsync(ct);
        return new(userId);
    }

    private static AuthFault OperationConflict() => new(409, "operation_id_reused", "This operation ID is already in use.");
}
