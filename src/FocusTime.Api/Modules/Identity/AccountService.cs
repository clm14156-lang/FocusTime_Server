using System.Security.Cryptography;
using FocusTime.Api.Modules.Sessions;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Identity;

public sealed class AccountService(AppDbContext db, UserManager<AppUser> users, AuthSecurity security,
    IEmailSender mail, TimeProvider clock, ILogger<AccountService> logger)
{
    // PostgreSQL timestamps have microsecond precision. Milliseconds also make replay responses exact.
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
    private static string Normalize(string email) => email.Trim().Normalize().ToUpperInvariant();
    private static AuthFault InvalidCode() => new(400, "invalid_code", "The verification code is invalid or expired.");
    private static AuthFault InvalidSession() => new(401, "invalid_session", "Please sign in again.");
    private static UserProfile Profile(AppUser user) => new(user.Id, user.Email!, user.EmailConfirmed);

    // Every mutation for one account uses the same transaction-scoped lock, across API instances.
    // Hash collisions only serialize unrelated accounts; they never grant access.
    private Task LockAsync(string email, CancellationToken ct)
    {
        return AccountTransactions.LockAsync(db, email, ct);
    }

    public async Task<EmailChallengeResponse> SendCodeAsync(string email, string purpose, CancellationToken ct)
    {
        email = Normalize(email);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(email, ct);
        var recent = await db.EmailChallenges.Where(x => x.Email == email &&
            x.CreatedAtUtc > Now.AddHours(-1)).ToListAsync(ct);
        var last = recent.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault();
        if (last is not null && last.CreatedAtUtc > Now.AddSeconds(-60) || recent.Count >= 5)
        {
            var until = recent.Count >= 5 ? recent.Min(x => x.CreatedAtUtc).AddHours(1)
                : last!.CreatedAtUtc.AddSeconds(60);
            throw new AuthFault(429, "email_rate_limited", "Please wait before requesting another code.",
                Math.Max(1, (int)Math.Ceiling((until - Now).TotalSeconds)));
        }
        var challenge = new EmailChallenge { Email = email, Purpose = purpose,
            CreatedAtUtc = Now, ExpiresAtUtc = Now.AddMinutes(10) };
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6",
            System.Globalization.CultureInfo.InvariantCulture);
        challenge.CodeHash = security.CodeHash(challenge, code);
        foreach (var previous in recent.Where(x => x.Purpose == purpose)) previous.Consumed = true;
        db.EmailChallenges.Add(challenge);
        // Unknown reset accounts receive the same challenge shape, but no email and no usable ticket.
        if (purpose == "register" || await users.FindByEmailAsync(email) is not null)
        {
            try { await mail.SendCodeAsync(email, purpose, code, ct); }
            catch (AuthFault fault) when (purpose == "reset" && fault.Code == "email_unavailable")
            {
                // Do not reveal account existence through different SMTP failure responses.
                logger.LogWarning("Password recovery email delivery failed. Retry after the email cooldown.");
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(challenge.Id, challenge.ExpiresAtUtc, 60);
    }

    private async Task<EmailChallenge> VerifyCodeAsync(Guid id, string purpose, string code,
        string? email, CancellationToken ct)
    {
        var found = await db.EmailChallenges.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (found is null) throw InvalidCode();
        await LockAsync(found.Email, ct);
        var challenge = await db.EmailChallenges.SingleAsync(x => x.Id == id, ct);
        if (challenge.Purpose != purpose || (email is not null && challenge.Email != Normalize(email)) ||
            challenge.Consumed || challenge.ExpiresAtUtc <= Now || challenge.Attempts >= 5)
            throw InvalidCode();
        challenge.Attempts++;
        if (!AuthSecurity.Equal(challenge.CodeHash, security.CodeHash(challenge, code)))
        {
            // Persist failed attempts even though the caller's transaction will roll back.
            await db.SaveChangesAsync(ct);
            await db.Database.CurrentTransaction!.CommitAsync(ct);
            throw InvalidCode();
        }
        return challenge;
    }

    public async Task<UserProfile> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var challenge = await VerifyCodeAsync(request.ChallengeId, "register", request.Code, request.Email, ct);
        if (await users.FindByEmailAsync(challenge.Email) is not null)
            throw new AuthFault(409, "email_registered", "This email is already registered.");
        var user = new AppUser { Email = request.Email.Trim(), UserName = request.Email.Trim(),
            EmailConfirmed = true, LockoutEnabled = true };
        Check(await users.CreateAsync(user, request.Password));
        challenge.Consumed = true;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Profile(user);
    }

    public async Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        security.EnsureConfigured();
        if (request.Device.InstallationId == Guid.Empty)
            throw new AuthFault(400, "invalid_device", "InstallationId is required.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(request.Email, ct);
        var user = await users.FindByEmailAsync(Normalize(request.Email));
        if (user is null)
        {
            // Match the expensive password hash path for unknown accounts.
            _ = users.PasswordHasher.HashPassword(new AppUser(), request.Password);
            throw new AuthFault(401, "invalid_credentials", "Email or password is incorrect.");
        }
        if (await users.IsLockedOutAsync(user))
            throw new AuthFault(429, "account_locked", "Too many failed attempts. Try again later.",
                Math.Max(1, (int)Math.Ceiling(((user.LockoutEnd ?? Now.AddMinutes(15)) - Now).TotalSeconds)));
        if (!await users.CheckPasswordAsync(user, request.Password) || !user.EmailConfirmed)
        {
            Check(await users.AccessFailedAsync(user));
            await tx.CommitAsync(ct);
            throw new AuthFault(401, "invalid_credentials", "Email or password is incorrect.");
        }
        Check(await users.ResetAccessFailedCountAsync(user));
        var active = db.LoginSessions.Where(x => x.UserId == user.Id && x.RevokedAtUtc == null &&
            x.ExpiresAtUtc > Now && x.SecurityStamp == user.SecurityStamp);
        if (await active.CountAsync(ct) >= 100)
            throw new AuthFault(409, "session_limit_reached", "Sign out an existing session before signing in again.");
        var device = await db.LoginDevices.SingleOrDefaultAsync(x => x.UserId == user.Id &&
            x.InstallationId == request.Device.InstallationId, ct);
        if (device is null)
        {
            device = new LoginDevice { UserId = user.Id, InstallationId = request.Device.InstallationId,
                CreatedAtUtc = Now };
            db.LoginDevices.Add(device);
        }
        else if (await active.CountAsync(x => x.DeviceId == device.Id, ct) >= 10)
            throw new AuthFault(409, "session_limit_reached", "Sign out an existing session on this device first.");
        device.Name = request.Device.Name;
        device.OperatingSystem = request.Device.OperatingSystem;
        device.ClientVersion = request.Device.ClientVersion;
        device.LastSeenAtUtc = Now;
        var session = new LoginSession { UserId = user.Id, DeviceId = device.Id,
            SecurityStamp = user.SecurityStamp!, CreatedAtUtc = Now, LastSeenAtUtc = Now, ExpiresAtUtc = Now.AddDays(30) };
        db.LoginSessions.Add(session);
        var response = Issue(session, user, AuthSecurity.RandomToken(), AuthSecurity.RandomToken());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return response;
    }

    private TokenResponse Issue(LoginSession session, AppUser user, string access, string refresh)
    {
        session.AccessHash = AuthSecurity.Hash(access);
        session.LastSeenAtUtc = Now;
        session.AccessExpiresAtUtc = new[] { Now.AddMinutes(15), session.ExpiresAtUtc }.Min();
        db.RefreshCredentials.Add(new RefreshCredential { SessionId = session.Id, TokenHash = AuthSecurity.Hash(refresh) });
        return Response(session, user, access, refresh);
    }
    private static TokenResponse Response(LoginSession session, AppUser user, string access, string refresh) =>
        new("Bearer", access, session.AccessExpiresAtUtc, refresh, session.ExpiresAtUtc,
            session.Id, session.DeviceId, Profile(user));

    public async Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty) throw new AuthFault(400, "invalid_request_id", "RequestId is required.");
        var hash = AuthSecurity.Hash(request.RefreshToken);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var owner = await (from token in db.RefreshCredentials where token.TokenHash == hash
            join ownerSession in db.LoginSessions on token.SessionId equals ownerSession.Id
            join ownerUser in db.Users on ownerSession.UserId equals ownerUser.Id select ownerUser.Email).SingleOrDefaultAsync(ct);
        if (owner is null) throw InvalidSession();
        await LockAsync(owner, ct);
        var credential = await db.RefreshCredentials.SingleAsync(x => x.TokenHash == hash, ct);
        var session = await db.LoginSessions.SingleAsync(x => x.Id == credential.SessionId, ct);
        var user = await db.Users.SingleAsync(x => x.Id == session.UserId, ct);
        if (session.RevokedAtUtc is not null || session.ExpiresAtUtc <= Now || session.SecurityStamp != user.SecurityStamp)
            throw InvalidSession();
        var access = security.Derive($"access:{request.RefreshToken}:{request.RequestId:N}");
        var refresh = security.Derive($"refresh:{request.RefreshToken}:{request.RequestId:N}");
        if (credential.UsedAtUtc is not null)
        {
            var successor = await db.RefreshCredentials.SingleAsync(x => x.Id == credential.SuccessorId, ct);
            if (credential.RequestId == request.RequestId && credential.UsedAtUtc > Now.AddSeconds(-60) &&
                successor.UsedAtUtc is null && AuthSecurity.Equal(session.AccessHash, AuthSecurity.Hash(access)))
                return Response(session, user, access, refresh);
            session.RevokedAtUtc = Now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new AuthFault(401, "refresh_reused", "The session was revoked. Please sign in again.");
        }
        credential.UsedAtUtc = Now;
        credential.RequestId = request.RequestId;
        var response = Issue(session, user, access, refresh);
        credential.SuccessorId = db.ChangeTracker.Entries<RefreshCredential>()
            .Single(x => x.State == EntityState.Added).Entity.Id;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return response;
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        var hash = AuthSecurity.Hash(refreshToken);
        var owner = await (from token in db.RefreshCredentials where token.TokenHash == hash
            join ownerSession in db.LoginSessions on token.SessionId equals ownerSession.Id
            join ownerUser in db.Users on ownerSession.UserId equals ownerUser.Id select ownerUser.Email).SingleOrDefaultAsync(ct);
        if (owner is null) return;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(owner, ct);
        var session = await (from token in db.RefreshCredentials where token.TokenHash == hash
            join item in db.LoginSessions on token.SessionId equals item.Id select item).SingleAsync(ct);
        session.RevokedAtUtc ??= Now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<PasswordResetTicket> VerifyResetAsync(VerifyPasswordResetRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var challenge = await VerifyCodeAsync(request.ChallengeId, "reset", request.Code, null, ct);
        if (await users.FindByEmailAsync(challenge.Email) is null) throw InvalidCode();
        var ticket = AuthSecurity.RandomToken();
        challenge.Consumed = true;
        challenge.TicketHash = AuthSecurity.Hash(ticket);
        challenge.TicketExpiresAtUtc = Now.AddMinutes(10);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(ticket, challenge.TicketExpiresAtUtc.Value);
    }

    public async Task CompleteResetAsync(CompletePasswordResetRequest request, CancellationToken ct)
    {
        var hash = AuthSecurity.Hash(request.ResetTicket);
        var found = await db.EmailChallenges.AsNoTracking().SingleOrDefaultAsync(x => x.TicketHash == hash, ct);
        if (found is null) throw new AuthFault(400, "invalid_reset_ticket", "The reset ticket is invalid or expired.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(found.Email, ct);
        var challenge = await db.EmailChallenges.SingleAsync(x => x.Id == found.Id, ct);
        if (challenge.TicketConsumed || challenge.TicketExpiresAtUtc <= Now)
            throw new AuthFault(400, "invalid_reset_ticket", "The reset ticket is invalid or expired.");
        var user = await users.FindByEmailAsync(challenge.Email) ?? throw InvalidCode();
        foreach (var validator in users.PasswordValidators) Check(await validator.ValidateAsync(users, user, request.NewPassword));
        user.PasswordHash = users.PasswordHasher.HashPassword(user, request.NewPassword);
        Check(await users.UpdateSecurityStampAsync(user));
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        challenge.TicketConsumed = true;
        // Invalidate every outstanding recovery capability, including previously issued tickets.
        await db.EmailChallenges.Where(x => x.Email == challenge.Email && x.Purpose == "reset")
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Consumed, true)
                .SetProperty(x => x.TicketConsumed, true), ct);
        await db.LoginSessions.Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAtUtc, Now), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new AuthFault(400, "password_policy_failed",
            "Use 8–128 characters with at least one letter and one digit.");
    }
}
