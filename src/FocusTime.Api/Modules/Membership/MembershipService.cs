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
    public async Task<MembershipResponse> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var version = await db.MembershipStates.AsNoTracking().Where(x => x.UserId == userId)
            .Select(x => (long?)x.Version).SingleOrDefaultAsync(ct) ?? 0;
        var grant = await db.MembershipGrants.AsNoTracking().Where(x => x.UserId == userId &&
            x.RevokedAtUtc == null && x.StartsAtUtc <= now && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now))
            .OrderByDescending(x => x.Kind == "lifetime").ThenByDescending(x => x.ExpiresAtUtc)
            .ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
        return new(grant is null ? MembershipKind.Normal : grant.Kind == "lifetime" ? MembershipKind.Lifetime : MembershipKind.Annual,
            grant is not null, grant?.StartsAtUtc, grant?.ExpiresAtUtc, version,
            grant is null ? [] : [EntitlementCodes.ForcedMode, EntitlementCodes.AutomaticBlocking], now)
        { Plan = grant?.Kind ?? "normal" };
    }

    public async Task<UserProfile> ApplyAsync(UserProfile profile, CancellationToken ct) => profile with
    { Membership = profile.EmailConfirmed ? (await GetAsync(profile.Id, ct)).Kind.ToString().ToLowerInvariant() : "normal" };

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
