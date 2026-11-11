using System.Security.Claims;
using System.Security.Cryptography;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Identity.Contracts;
using FocusTime.Api.Modules.Membership;
using FocusTime.Api.Modules.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.AccountSecurity;

public sealed class AccountSecurityService(AppDbContext db, UserManager<AppUser> users,
    AccountService accounts, AuthSecurity security, IEmailSender mail, TimeProvider clock)
{
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
    private static string Normalize(string email) => email.Trim().Normalize().ToUpperInvariant();
    private static AuthFault InvalidCode() => new(400, "invalid_code", "The verification code is invalid or expired.");
    private static AuthFault InvalidProof() => new(400, "verification_expired", "Please verify your current email again.");
    private bool failureCommitted;

    private void Audit(Guid user, Guid session, string operation, string? failure = null) =>
        db.AccountSecurityAudits.Add(new() { UserId = user, SessionId = session, Operation = operation,
            Result = failure is null ? "success" : "failed", FailureCode = failure, OccurredAtUtc = Now });

    private async Task<T> Run<T>(ClaimsPrincipal principal, string authorization, string operation,
        Func<AppUser, LoginSession, CancellationToken, Task<T>> work, CancellationToken ct, string? targetEmail = null)
    {
        var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var sessionId = Guid.Parse(principal.FindFirstValue("sid")!);
        failureCommitted = false;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var email = await db.Users.Where(x => x.Id == userId).Select(x => x.Email).SingleAsync(ct);
            // Canonical order prevents deadlocks when two accounts attempt an email swap.
            foreach (var item in new[] { email!, targetEmail ?? email! }.Select(Normalize).Distinct().Order(StringComparer.Ordinal))
                await AccountTransactions.LockAsync(db, item, ct);
            await AccountTransactions.LockSessionAsync(db, clock, principal, authorization, ct);
            var user = await db.Users.SingleAsync(x => x.Id == userId, ct);
            if (user.ClosedAtUtc is not null) throw new AuthFault(401, "invalid_session", "Please sign in again.");
            var session = await db.LoginSessions.SingleAsync(x => x.Id == sessionId, ct);
            var result = await work(user, session, ct);
            Audit(userId, sessionId, operation);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return result;
        }
        catch (AuthFault fault) when (!failureCommitted)
        {
            db.ChangeTracker.Clear();
            Audit(userId, sessionId, operation, fault.Code);
            await db.SaveChangesAsync(ct);
            throw;
        }
    }

    private async Task FailPersisted(AppUser user, LoginSession session, string operation, AuthFault fault, CancellationToken ct)
    {
        Audit(user.Id, session.Id, operation, fault.Code);
        await db.SaveChangesAsync(ct); await db.Database.CurrentTransaction!.CommitAsync(ct);
        failureCommitted = true;
        throw fault;
    }

    private bool Bound(SecurityChallenge c, AppUser user, LoginSession session, string purpose) =>
        c.UserId == user.Id && c.SessionId == session.Id && c.Purpose == purpose &&
        AuthSecurity.Equal(c.StampHash, AuthSecurity.Hash(user.SecurityStamp!));
    private string CodeHash(SecurityChallenge c, string code) => security.Derive(
        $"account-security:{c.Id:N}:{c.UserId:N}:{c.SessionId:N}:{c.Purpose}:{c.Email}:{c.StampHash}:{code}");

    private async Task<SecurityChallenge> Code(AppUser user, LoginSession session, string purpose,
        VerifySecurityCode request, string operation, CancellationToken ct)
    {
        var c = await db.SecurityChallenges.SingleOrDefaultAsync(x => x.Id == request.ChallengeId, ct);
        if (c is null || !Bound(c, user, session, purpose) || c.Consumed || c.ExpiresAtUtc <= Now || c.Attempts >= 5)
            throw InvalidCode();
        c.Attempts++;
        if (!AuthSecurity.Equal(c.CodeHash, CodeHash(c, request.Code)))
            await FailPersisted(user, session, operation, InvalidCode(), ct);
        c.Consumed = true;
        return c;
    }

    private async Task<SecurityChallenge> Proof(AppUser user, LoginSession session, string purpose, string ticket, CancellationToken ct)
    {
        var hash = AuthSecurity.Hash(ticket);
        var c = await db.SecurityChallenges.SingleOrDefaultAsync(x => x.ProofHash == hash, ct);
        if (c is null || !Bound(c, user, session, purpose) || !c.Consumed || c.ProofConsumed || c.ProofExpiresAtUtc is null || c.ProofExpiresAtUtc <= Now)
            throw InvalidProof();
        return c;
    }

    private async Task<EmailChallengeResponse> Send(AppUser user, LoginSession session, string email,
        string purpose, SecurityChallenge? parent, CancellationToken ct)
    {
        email = Normalize(email);
        var recent = await db.SecurityChallenges.Where(x => (x.UserId == user.Id || x.Email == email) && x.CreatedAtUtc > Now.AddHours(-1))
            .Select(x => x.CreatedAtUtc).ToListAsync(ct);
        recent.AddRange(await db.EmailChallenges.Where(x => x.Email == email && x.CreatedAtUtc > Now.AddHours(-1))
            .Select(x => x.CreatedAtUtc).ToListAsync(ct));
        var targetRecent = await db.SecurityChallenges.Where(x => x.Email == email && x.CreatedAtUtc > Now.AddSeconds(-60))
            .Select(x => x.CreatedAtUtc).ToListAsync(ct);
        targetRecent.AddRange(await db.EmailChallenges.Where(x => x.Email == email && x.CreatedAtUtc > Now.AddSeconds(-60))
            .Select(x => x.CreatedAtUtc).ToListAsync(ct));
        if (recent.Count >= 5 || targetRecent.Count > 0)
        {
            var until = recent.Count >= 5 ? recent.Min().AddHours(1) : targetRecent.Max().AddSeconds(60);
            throw new AuthFault(429, "email_rate_limited", "Please wait before requesting another code.",
                Math.Max(1, (int)Math.Ceiling((until - Now).TotalSeconds)));
        }
        await db.SecurityChallenges.Where(x => x.UserId == user.Id && x.Purpose == purpose)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Consumed, true).SetProperty(x => x.ProofConsumed, true), ct);
        var challenge = new SecurityChallenge { UserId = user.Id, SessionId = session.Id, Email = email,
            Purpose = purpose, StampHash = AuthSecurity.Hash(user.SecurityStamp!), ParentId = parent?.Id,
            CreatedAtUtc = Now, ExpiresAtUtc = parent?.ProofExpiresAtUtc < Now.AddMinutes(10) ? parent.ProofExpiresAtUtc.Value : Now.AddMinutes(10) };
        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        challenge.CodeHash = CodeHash(challenge, code);
        db.SecurityChallenges.Add(challenge);
        try { await mail.SendCodeAsync(email, purpose, code, ct); }
        catch (AuthFault fault) when (fault.Code == "email_unavailable")
        {
            // Failed delivery still consumes a send slot, preventing repeated SMTP requests.
            challenge.Consumed = true;
            await FailPersisted(user, session, purpose + ".code", fault, ct);
        }
        return new(challenge.Id, challenge.ExpiresAtUtc, 60);
    }

    public Task<EmailChallengeResponse> SendCurrentCodeAsync(ClaimsPrincipal p, string auth, string purpose, CancellationToken ct) =>
        Run(p, auth, purpose + ".code", (u, s, token) => Send(u, s, u.Email!, purpose, null, token), ct);

    public Task<SecurityProof> VerifyAsync(ClaimsPrincipal p, string auth, string purpose, VerifySecurityCode request, CancellationToken ct) =>
        Run(p, auth, purpose + ".verify", async (u, s, token) =>
        {
            var challenge = await Code(u, s, purpose, request, purpose + ".verify", token);
            var ticket = AuthSecurity.RandomToken();
            challenge.ProofHash = AuthSecurity.Hash(ticket); challenge.ProofExpiresAtUtc = Now.AddMinutes(10);
            return new SecurityProof(ticket, challenge.ProofExpiresAtUtc.Value);
        }, ct);

    public Task<EmailChallengeResponse> SendNewEmailCodeAsync(ClaimsPrincipal p, string auth, NewEmailCode request, CancellationToken ct) =>
        Run(p, auth, "email-new.code", async (u, s, token) =>
        {
            var parent = await Proof(u, s, "email-current", request.Ticket, token);
            await CheckNewEmail(u, request.NewEmail, token);
            return await Send(u, s, request.NewEmail, "email-new", parent, token);
        }, ct, request.NewEmail);

    private async Task CheckNewEmail(AppUser user, string email, CancellationToken ct)
    {
        if (Normalize(email) == user.NormalizedEmail) throw new AuthFault(400, "email_unchanged", "Use a different email address.");
        if (await db.Users.AnyAsync(x => x.NormalizedEmail == Normalize(email), ct))
            throw new AuthFault(409, "email_registered", "This email is already registered.");
    }

    public async Task<SecuritySessionResponse> ChangeEmailAsync(ClaimsPrincipal p, string auth, CompleteEmailChange request, CancellationToken ct)
    {
        var userId = Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var target = await db.SecurityChallenges.AsNoTracking().Where(x => x.Id == request.ChallengeId && x.UserId == userId)
            .Select(x => x.Email).SingleOrDefaultAsync(ct);
        return await Run(p, auth, "email.change", async (u, s, token) =>
        {
            var parent = await Proof(u, s, "email-current", request.Ticket, token);
            var challenge = await Code(u, s, "email-new", new(request.ChallengeId, request.Code), "email.change", token);
            if (challenge.ParentId != parent.Id) throw InvalidCode();
            await CheckNewEmail(u, challenge.Email, token);
            var previous = u.Email!;
            u.Email = challenge.Email.ToLowerInvariant(); u.NormalizedEmail = challenge.Email;
            u.UserName = u.Email; u.NormalizedUserName = challenge.Email; u.EmailConfirmed = true;
            Check(await users.UpdateSecurityStampAsync(u));
            await Invalidate(u, previous, s.Id, token);
            QueueNotice(u, previous, "FocusTime 登录邮箱修改通知", "您的登录邮箱已修改。如非本人操作，请及时联系支持。其他设备已退出登录。");
            QueueNotice(u, u.Email, "FocusTime 登录邮箱修改成功", "此邮箱现已绑定到您的 FocusTime 账号。其他设备需使用新邮箱重新登录。");
            return new SecuritySessionResponse(await accounts.ReplaceSessionCredentialsAsync(u, s, token));
        }, ct, target);
    }

    public Task<SecuritySessionResponse> ChangePasswordAsync(ClaimsPrincipal p, string auth, ChangeLoginPassword request, CancellationToken ct) =>
        Run(p, auth, "password.change", async (u, s, token) =>
        {
            if (u.LockoutEnd > Now) throw new AuthFault(429, "account_locked", "Try again later.", Math.Max(1, (int)(u.LockoutEnd.Value - Now).TotalSeconds));
            if (!await users.CheckPasswordAsync(u, request.CurrentPassword))
            {
                Check(await users.AccessFailedAsync(u));
                await FailPersisted(u, s, "password.change", new(400, "current_password_incorrect", "Current password is incorrect."), token);
            }
            if (request.CurrentPassword == request.NewPassword) throw new AuthFault(400, "password_unchanged", "Use a different password.");
            foreach (var validator in users.PasswordValidators) Check(await validator.ValidateAsync(users, u, request.NewPassword));
            u.PasswordHash = users.PasswordHasher.HashPassword(u, request.NewPassword);
            u.AccessFailedCount = 0; u.LockoutEnd = null;
            Check(await users.UpdateSecurityStampAsync(u));
            await Invalidate(u, u.Email!, s.Id, token);
            QueueNotice(u, u.Email!, "FocusTime 密码修改通知", "您的登录密码已修改。当前设备保持登录，其他设备已退出。如非本人操作，请立即找回密码并联系支持。");
            return new SecuritySessionResponse(await accounts.ReplaceSessionCredentialsAsync(u, s, token));
        }, ct);

    private async Task Invalidate(AppUser u, string previousEmail, Guid? keepSession, CancellationToken ct)
    {
        await db.LoginSessions.Where(x => x.UserId == u.Id && x.Id != keepSession && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, Now), ct);
        await db.EmailChallenges.Where(x => (x.Email == Normalize(previousEmail) || x.Email == u.NormalizedEmail) && x.Purpose == "reset")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Consumed, true).SetProperty(x => x.TicketConsumed, true), ct);
        await db.SecurityChallenges.Where(x => x.UserId == u.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Consumed, true).SetProperty(x => x.ProofConsumed, true), ct);
        // ExecuteUpdate bypasses tracked verification entities. Do not overwrite consumed flags at SaveChanges.
        foreach (var entry in db.ChangeTracker.Entries<SecurityChallenge>().ToArray()) entry.State = EntityState.Detached;
    }

    private void QueueNotice(AppUser u, string recipient, string subject, string body) =>
        db.SecurityNotifications.Add(new() { UserId = u.Id, Recipient = recipient, Subject = subject, Body = body,
            CreatedAtUtc = Now, NextAttemptAtUtc = Now, ExpiresAtUtc = Now.AddDays(1) });

    public Task<bool> CloseAsync(ClaimsPrincipal p, string auth, CloseAccount request, CancellationToken ct) =>
        Run(p, auth, "account.close", async (u, s, token) =>
        {
            if (request.Confirmation != "CLOSE") throw new AuthFault(400, "confirmation_required", "Explicit confirmation is required.");
            await Proof(u, s, "closure", request.Ticket, token);
            u.ClosedAtUtc = Now; u.PasswordHash = null; u.EmailConfirmed = false;
            Check(await users.UpdateSecurityStampAsync(u));
            await Invalidate(u, u.Email!, null, token);
            await db.MembershipGrants.Where(x => x.UserId == u.Id && x.RevokedAtUtc == null)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAtUtc, Now), token);
            var state = await db.MembershipStates.SingleOrDefaultAsync(x => x.UserId == u.Id, token);
            if (state is null) db.MembershipStates.Add(state = new() { UserId = u.Id });
            state.Version++;
            // Soft closure deliberately retains cloud records, orders, and security audit history.
            return true;
        }, ct);

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new AuthFault(400, "password_policy_failed", "Use 8–128 characters with a letter and a digit.");
    }
}
