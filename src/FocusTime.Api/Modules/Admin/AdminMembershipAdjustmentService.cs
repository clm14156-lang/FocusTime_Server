using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Membership;
using FocusTime.Api.Modules.Sessions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FocusTime.Api.Modules.Admin;

public sealed class AdminMembershipAdjustmentService(AppDbContext db, MembershipService memberships, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, string> GrantReasons = new() {
        ["compensation"] = "用户补偿", ["promotion"] = "活动赠送", ["trial"] = "免费体验", ["internal-test"] = "内部测试" };
    private static readonly Dictionary<string, string> RevokeReasons = new() {
        ["issued-in-error"] = "发放错误", ["ineligible"] = "赠送资格不符", ["test-ended"] = "内部测试结束",
        ["refund-request"] = "用户申请退款", ["other"] = "其他原因" };
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
    private static AuthFault Conflict(string code, string message) => new(409, code, message);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private sealed record Snapshot(List<MembershipGrant> Grants, HashSet<Guid> Gifts, HashSet<Guid> Purchased,
        AdminEffectiveMembership Current, string Fingerprint, Guid? TrialGrantId);

    private async Task<AppUser> FindUser(Guid id, CancellationToken ct) =>
        await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AuthFault(404, "user_not_found", "User not found.");

    private static AdminEffectiveMembership Effective(IEnumerable<MembershipGrant> grants, DateTimeOffset now, Guid? trialGrantId = null)
    {
        var grant = grants.Where(x => x.RevokedAtUtc == null && x.StartsAtUtc <= now && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now))
            .OrderByDescending(x => x.Kind == "lifetime").ThenByDescending(x => x.ExpiresAtUtc).ThenBy(x => x.Id).FirstOrDefault();
        return new(grant is not null, grant?.Kind, grant?.Id, grant?.StartsAtUtc, grant?.ExpiresAtUtc)
        {
            IsTrial = grant is { ExpiresAtUtc: not null } && grant.Id == trialGrantId
        };
    }

    private async Task<Snapshot> ReadSnapshot(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var grants = await db.MembershipGrants.AsNoTracking().Where(x => x.UserId == id && x.RevokedAtUtc == null &&
            (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now)).OrderBy(x => x.Id).ToListAsync(ct);
        var ids = grants.Select(x => x.Id).ToArray();
        // Order-linked grants are excluded from the legacy gift-only scope and included in the confirmation fingerprint.
        var purchased = (await db.MembershipOrders.AsNoTracking().Where(x => x.MembershipGrantId != null && ids.Contains(x.MembershipGrantId.Value))
            .Select(x => x.MembershipGrantId!.Value).Distinct().ToArrayAsync(ct)).ToHashSet();
        var gifts = (await db.AdminMembershipAdjustments.AsNoTracking().Where(x => x.UserId == id && x.State == "applied" &&
            x.Action == "grant" && x.GrantId != null && ids.Contains(x.GrantId.Value)).Select(x => x.GrantId!.Value).ToArrayAsync(ct)).ToHashSet();
        gifts.ExceptWith(purchased);
        var version = await db.MembershipStates.AsNoTracking().Where(x => x.UserId == id).Select(x => (long?)x.Version).SingleOrDefaultAsync(ct) ?? 0;
        Guid? trialGrantId = ids.Contains(id) &&
            await MembershipService.RegistrationTrialAudits(db).AnyAsync(x => x.UserId == id, ct) ? id : null;
        var current = Effective(grants, now, trialGrantId);
        // Display metadata does not invalidate confirmations issued before the label was added.
        var fingerprint = AuthSecurity.Hash(Serialize(new { version, current = current with { IsTrial = false },
            grants = grants.Select(x => new { x.Id, x.Kind, x.StartsAtUtc, x.ExpiresAtUtc }),
            gifts = gifts.Order(), purchased = purchased.Order() }));
        return new(grants, gifts, purchased, current, fingerprint, trialGrantId);
    }

    public async Task<AdminMembershipAdjustmentContext> Context(Guid userId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var user = await FindUser(userId, ct);
        var now = Now;
        var state = await ReadSnapshot(userId, now, ct);
        var protectedCount = state.Grants.Count(x => !state.Gifts.Contains(x.Id));
        var canAll = user.EmailConfirmed && state.Grants.Count > 0;
        var message = !user.EmailConfirmed ? "该用户尚未验证邮箱，暂不能调整会员。" : state.Purchased.Count > 0
            ? "包含订单购买权益。撤销不等于退款。" : null;
        var result = new AdminMembershipAdjustmentContext(userId, user.Email!, state.Current,
            Effective(state.Grants.Where(x => !state.Gifts.Contains(x.Id)), now, state.TrialGrantId), canAll ? Effective([], now) : null,
            state.Gifts.Count, protectedCount, user.EmailConfirmed, canAll, message);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<AppUser> LockAndAuthenticate(Guid targetId, ClaimsPrincipal principal, string authorization, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || authorization.Length != 71)
            throw new AuthFault(401, "session_expired", "Please sign in again.");
        var actor = await FindUser(actorId, ct);
        var target = await FindUser(targetId, ct);
        // Canonical order avoids deadlocks when two administrators act on one another.
        foreach (var email in new[] { actor.Email!, target.Email! }.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
            await AccountTransactions.LockAsync(db, email, ct);
        await AccountTransactions.LockSessionAsync(db, clock, principal, authorization, ct);
        var currentActor = await FindUser(actorId, ct);
        if (currentActor.LockoutEnabled && currentActor.LockoutEnd > Now)
            throw new AuthFault(403, "admin_actor_locked", "A locked administrator cannot adjust membership.");
        target = await FindUser(targetId, ct);
        if (!target.EmailConfirmed) throw Conflict("user_email_unconfirmed", "Target user must have a verified email.");
        return target;
    }

    private static PreviewAdminMembershipRequest Normalize(PreviewAdminMembershipRequest value)
    {
        var note = string.IsNullOrWhiteSpace(value.Note) ? null : value.Note.Trim();
        if (value.OperationId == Guid.Empty || note?.Length > 400 || note?.Contains('\0') == true || value.Action is not ("grant" or "revoke") ||
            value.Action == "grant" && (value.Plan is not ("monthly" or "annual" or "lifetime") || value.Scope != null || !GrantReasons.ContainsKey(value.ReasonCode)) ||
            value.Action == "revoke" && (value.Plan != null || value.Scope is not ("all" or "admin-gifts") || !RevokeReasons.ContainsKey(value.ReasonCode)) ||
            value.Action == "revoke" && value.ReasonCode == "other" && note is null)
            throw new AuthFault(400, "invalid_membership_adjustment", "Invalid membership adjustment or missing required note.");
        return value with { Note = note };
    }

    public async Task<AdminMembershipAdjustmentView> Preview(Guid userId, PreviewAdminMembershipRequest input,
        ClaimsPrincipal principal, string authorization, CancellationToken ct)
    {
        var request = Normalize(input);
        var actorId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var hash = AuthSecurity.Hash(Serialize(new { userId, actorId, request }));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAndAuthenticate(userId, principal, authorization, ct);
        var previous = await db.AdminMembershipAdjustments.SingleOrDefaultAsync(x => x.UserId == userId && x.OperationId == request.OperationId, ct);
        if (previous is not null)
        {
            if (previous.ActorUserId != actorId || previous.RequestHash != hash) throw Conflict("operation_id_reused", "Operation ID already used for a different request.");
            if (previous.State != "applied" && previous.ExpiresAtUtc <= Now) throw Conflict("preview_expired", "Preview expired. Create a new preview.");
            await tx.CommitAsync(ct);
            return View(previous);
        }
        if (await db.AdminMembershipAdjustments.AsNoTracking().AnyAsync(x => x.OperationId == request.OperationId, ct) ||
            await db.MembershipGrants.AsNoTracking().AnyAsync(x => x.Id == request.OperationId, ct))
            throw Conflict("operation_id_reused", "Operation ID is already in use.");
        var now = Now;
        var snapshot = await ReadSnapshot(userId, now, ct);
        Guid[] targets = [];
        MembershipGrant? grant = null;
        if (request.Action == "grant")
        {
            var latest = snapshot.Grants.Where(x => x.StartsAtUtc <= now && x.ExpiresAtUtc != null).Max(x => x.ExpiresAtUtc) ?? now;
            DateTimeOffset? expires = null;
            if (request.Plan != "lifetime")
            {
                try { expires = latest.AddDays(request.Plan == "monthly" ? 30 : 365); }
                catch (ArgumentOutOfRangeException) { throw Conflict("membership_expiry_out_of_range", "Membership expiry exceeds supported range."); }
            }
            grant = new() { Id = request.OperationId, UserId = userId, Kind = request.Plan!, StartsAtUtc = now, ExpiresAtUtc = expires, CreatedAtUtc = now };
        }
        else
        {
            targets = request.Scope == "admin-gifts" ? snapshot.Gifts.Order().ToArray() : snapshot.Grants.Select(x => x.Id).ToArray();
            if (targets.Length == 0)
                throw request.Scope == "admin-gifts"
                    ? Conflict("no_admin_gifts", "No verified administrator gifts to revoke.")
                    : Conflict("no_active_membership", "User has no revocable membership.");
        }
        var after = Effective(request.Action == "grant" ? snapshot.Grants.Append(grant!) : snapshot.Grants.Where(x => !targets.Contains(x.Id)),
            now, snapshot.TrialGrantId);
        var receipt = new AdminMembershipAdjustment {
            UserId = userId, OperationId = request.OperationId, ActorUserId = actorId, RequestHash = hash,
            Action = request.Action, Plan = request.Plan, Scope = request.Scope, ReasonCode = request.ReasonCode, Note = request.Note,
            ExpectedFingerprint = snapshot.Fingerprint, BeforeJson = Serialize(snapshot.Current), AfterJson = Serialize(after),
            TargetGrantIdsJson = Serialize(targets), GrantId = grant?.Id, GrantStartsAtUtc = grant?.StartsAtUtc, GrantExpiresAtUtc = grant?.ExpiresAtUtc,
            CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(5) };
        db.AdminMembershipAdjustments.Add(receipt);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw Conflict("operation_id_reused", "Operation ID is already in use."); }
        await tx.CommitAsync(ct);
        return View(receipt);
    }

    public async Task<AdminMembershipAdjustmentView> Apply(Guid userId, Guid operationId, bool acknowledgeNoRefund, ClaimsPrincipal principal,
        string authorization, CancellationToken ct)
    {
        if (operationId == Guid.Empty) throw new AuthFault(400, "invalid_operation_id", "Operation ID is required.");
        var actorId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAndAuthenticate(userId, principal, authorization, ct);
        var receipt = await db.AdminMembershipAdjustments.SingleOrDefaultAsync(x => x.UserId == userId && x.OperationId == operationId, ct)
            ?? throw new AuthFault(404, "preview_not_found", "Preview not found.");
        if (receipt.ActorUserId != actorId) throw Conflict("preview_actor_mismatch", "Preview belongs to another administrator.");
        if (receipt.State == "applied") { await tx.CommitAsync(ct); return View(receipt); }
        var now = Now;
        if (receipt.ExpiresAtUtc <= now) throw Conflict("preview_expired", "Preview expired. Create a new preview.");
        if (receipt.Action == "revoke" && receipt.Scope == "all" && !acknowledgeNoRefund)
            throw Conflict("revocation_confirmation_required", "Confirm that revoking all entitlements does not issue a refund.");
        var snapshot = await ReadSnapshot(userId, now, ct);
        if (snapshot.Fingerprint != receipt.ExpectedFingerprint) throw Conflict("membership_changed", "Membership changed. Review a new preview.");
        if (!await db.MembershipStates.AnyAsync(x => x.UserId == userId, ct))
        {
            db.MembershipStates.Add(new() { UserId = userId });
            await db.SaveChangesAsync(ct);
        }
        var reason = (receipt.Action == "grant" ? GrantReasons : RevokeReasons)[receipt.ReasonCode] + (receipt.Note is null ? "" : "：" + receipt.Note);
        var actor = "admin-adjustment:" + actorId;
        long version;
        if (receipt.Action == "grant")
        {
            var audit = await memberships.StageChangeAsync(userId, operationId, "grant", receipt.Plan!, receipt.GrantStartsAtUtc!.Value,
                receipt.GrantExpiresAtUtc, null, reason, actor, ct);
            version = audit.Version;
        }
        else
        {
            var targets = JsonSerializer.Deserialize<Guid[]>(receipt.TargetGrantIdsJson, Json)!;
            HashSet<Guid> eligible = receipt.Scope switch
            {
                "all" => snapshot.Grants.Select(grant => grant.Id).ToHashSet(),
                "admin-gifts" => snapshot.Gifts,
                _ => []
            };
            if (targets.Length == 0 || targets.Length != eligible.Count || !eligible.SetEquals(targets))
                throw Conflict("membership_changed", "Revocation targets changed. Review a new preview.");
            version = 0;
            foreach (var id in targets)
            {
                var childId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"admin-revoke:{operationId:N}:{id:N}"))[..16]);
                var audit = await memberships.StageChangeAsync(userId, childId, "revoke", "", DateTimeOffset.UnixEpoch,
                    null, id, reason, actor, ct);
                version = audit.Version;
            }
            if (snapshot.Purchased.Overlaps(targets))
            {
                var orderIds = await db.MembershipOrders.AsNoTracking()
                    .Where(order => order.UserId == userId && order.MembershipGrantId.HasValue && targets.Contains(order.MembershipGrantId.Value))
                    .Select(order => order.Id).ToArrayAsync(ct);
                db.MembershipOrderEvents.AddRange(orderIds.Select(orderId => new MembershipOrderEvent
                {
                    OrderId = orderId, Kind = "membership_revoked", Actor = actor, OccurredAtUtc = now
                }));
            }
        }
        receipt.State = "applied";
        receipt.AppliedAtUtc = now;
        receipt.AppliedVersion = version;
        // Receipt, grants, membership version and audit commit atomically. No payment order is created or changed.
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return View(receipt);
    }

    private static AdminMembershipAdjustmentView View(AdminMembershipAdjustment receipt) => new(receipt.UserId, receipt.OperationId,
        receipt.Action, receipt.Plan, receipt.Scope, receipt.ReasonCode, receipt.Note,
        JsonSerializer.Deserialize<AdminEffectiveMembership>(receipt.BeforeJson, Json)!,
        JsonSerializer.Deserialize<AdminEffectiveMembership>(receipt.AfterJson, Json)!,
        receipt.Action == "grant" ? 1 : JsonSerializer.Deserialize<Guid[]>(receipt.TargetGrantIdsJson, Json)!.Length,
        receipt.ExpiresAtUtc, receipt.State == "applied", receipt.AppliedVersion);
}
