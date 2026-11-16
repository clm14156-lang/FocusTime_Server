using System.Text.Json;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Identity.Contracts;
using FocusTime.Api.Modules.Membership.Contracts;
using FocusTime.Api.Modules.Sessions;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Membership;

public sealed class MembershipService(AppDbContext db, TimeProvider clock)
{
    private const string RegistrationTrialActor = "registration-trial";
    private const string RegistrationTrialReason = "免费体验";

    internal static IQueryable<MembershipAudit> RegistrationTrialAudits(AppDbContext db) =>
        db.MembershipAudits.AsNoTracking().Where(x => x.Actor == RegistrationTrialActor &&
            x.Action == "grant" && x.Reason == RegistrationTrialReason &&
            x.GrantId == x.UserId && x.OperationId == x.UserId);

    internal async Task StageRegistrationTrialAsync(Guid userId, CancellationToken ct)
    {
        // One operation per new account. The audit and grant primary keys also enforce uniqueness.
        var operationId = userId;
        if (await db.MembershipAudits.AsNoTracking().AnyAsync(x => x.UserId == userId &&
            x.OperationId == operationId && x.Actor == RegistrationTrialActor, ct)) return;

        var days = await db.RegistrationTrialRules.AsNoTracking().Where(x => x.Id == RegistrationTrialRule.SingletonId)
            .Select(x => x.DurationDays).SingleAsync(ct);
        var starts = clock.GetUtcNow();
        await StageChangeAsync(userId, operationId, "grant", "monthly", starts, starts.AddDays(days),
            null, RegistrationTrialReason, RegistrationTrialActor, ct);
    }

    public async Task<MembershipResponse> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var version = await db.MembershipStates.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => (long?)x.Version).SingleOrDefaultAsync(ct) ?? 0;
        var grant = await db.MembershipGrants.AsNoTracking().Where(x => x.UserId == userId &&
            x.RevokedAtUtc == null && x.StartsAtUtc <= now && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now))
            .OrderByDescending(x => x.Kind == "lifetime").ThenByDescending(x => x.ExpiresAtUtc)
            .ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
        // Display metadata only: retain the existing effective-grant selection and entitlement rules.
        var isTrial = grant is { ExpiresAtUtc: not null } &&
            ((grant.Id == userId && await RegistrationTrialAudits(db).AnyAsync(x => x.UserId == userId, ct)) ||
            await db.AdminMembershipAdjustments.AsNoTracking().AnyAsync(x => x.UserId == userId &&
                x.GrantId == grant.Id && x.State == "applied" && x.Action == "grant" && x.ReasonCode == "trial" &&
                !db.MembershipOrders.Any(order => order.MembershipGrantId == grant.Id), ct));
        DateTimeOffset? lastExpiredAt = grant is not null ? null : await db.MembershipGrants.AsNoTracking()
            .Where(x => x.UserId == userId && x.RevokedAtUtc == null && x.ExpiresAtUtc <= now)
            .MaxAsync(x => x.ExpiresAtUtc, ct);
        return new(grant is null ? MembershipKind.Normal : grant.Kind == "lifetime" ? MembershipKind.Lifetime : MembershipKind.Annual,
            grant is not null, grant?.StartsAtUtc, grant?.ExpiresAtUtc, version,
            grant is null ? [] : [EntitlementCodes.ForcedMode, EntitlementCodes.AutomaticBlocking], now)
        {
            Plan = grant?.Kind ?? "normal",
            IsTrial = isTrial,
            State = grant is not null ? "active" : lastExpiredAt.HasValue ? "expired" : "normal",
            LastExpiredAtUtc = lastExpiredAt
        };
    }

    public async Task<UserProfile> ApplyAsync(UserProfile profile, CancellationToken ct, bool includeStatus = false)
    {
        if (!profile.EmailConfirmed) return profile;
        var status = await GetAsync(profile.Id, ct);
        return profile with
        {
            Membership = status.Kind.ToString().ToLowerInvariant(),
            MembershipVersion = status.Version,
            MembershipStatus = includeStatus ? status : null
        };
    }

    public async Task<MembershipAudit> ChangeAsync(Guid userId, Guid operationId, string action, string kind,
        DateTimeOffset starts, DateTimeOffset? expires, Guid? grantId, string reason, string actor, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var email = await db.Users.Where(x => x.Id == userId && x.EmailConfirmed).Select(x => x.Email).SingleOrDefaultAsync(ct)
            ?? throw new ArgumentException("Confirmed user not found.");
        await AccountTransactions.LockAsync(db, email, ct);
        var audit = await StageChangeAsync(userId, operationId, action, kind, starts, expires, grantId, reason, actor, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return audit;
    }

    // The caller holds the account transaction lock and commits both the grant and any related changes.
    internal async Task<MembershipAudit> StageChangeAsync(Guid userId, Guid operationId, string action, string kind,
        DateTimeOffset starts, DateTimeOffset? expires, Guid? grantId, string reason, string actor, CancellationToken ct)
    {
        if (userId == Guid.Empty || operationId == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Length > 512 ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 128 || action is not ("grant" or "revoke") ||
            action == "grant" && (kind is not ("monthly" or "annual" or "lifetime") ||
                (kind == "lifetime" ? expires is not null : expires is null || expires <= starts)))
            throw new ArgumentException("Invalid membership operation. Supply a reason, operation ID and valid dates/kind.");
        var hash = AuthSecurity.Hash(JsonSerializer.Serialize(new { action, kind, starts, expires, grantId, reason, actor }));
        var receipt = await db.MembershipAudits.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId && x.OperationId == operationId, ct);
        if (receipt is not null)
        {
            if (receipt.RequestHash != hash) throw new ArgumentException("Operation ID was already used for a different change.");
            return receipt;
        }
        var state = await db.MembershipStates.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (state is null) { state = new() { UserId = userId }; db.MembershipStates.Add(state); }
        state.Version++;
        var now = clock.GetUtcNow();
        if (action == "grant")
        {
            grantId = operationId;
            db.MembershipGrants.Add(new() { Id = grantId.Value, UserId = userId, Kind = kind,
                StartsAtUtc = starts, ExpiresAtUtc = expires, CreatedAtUtc = now });
        }
        else
        {
            if (grantId is not null && !await db.MembershipGrants.AnyAsync(x => x.Id == grantId && x.UserId == userId, ct))
                throw new ArgumentException("Grant does not belong to this user.");
            await db.MembershipGrants.Where(x => x.UserId == userId && (grantId == null || x.Id == grantId) && x.RevokedAtUtc == null)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAtUtc, now), ct);
        }
        var audit = new MembershipAudit { UserId = userId, OperationId = operationId, GrantId = grantId,
            Action = action, RequestHash = hash, Reason = reason, Actor = actor, Version = state.Version, CreatedAtUtc = now };
        db.MembershipAudits.Add(audit);
        return audit;
    }
}
