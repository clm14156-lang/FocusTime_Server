using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Identity;

public sealed class LoginAttempt
{
    public required string AccountKey { get; init; }
    public DateTimeOffset WindowStartedAtUtc { get; set; }
    public int RequestCount { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? LastFailureAtUtc { get; set; }
}

public sealed class LoginCaptcha
{
    public required string AccountKey { get; init; }
    public Guid InstallationId { get; init; }
    public Guid ChallengeId { get; set; }
    public string AnswerHash { get; set; } = "";
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

public sealed class LoginProtection(AppDbContext db, AuthSecurity security, TimeProvider clock)
{
    private const int FailureThreshold = 3;
    private const int RequestLimit = 12;
    private static readonly TimeSpan RequestWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    // Called inside the account transaction. Row locks also serialize against expiry cleanup.
    internal async Task<LoginAttempt> BeginRequestAsync(string normalizedEmail, CancellationToken ct)
    {
        var key = security.Derive($"login-account:{normalizedEmail}");
        var now = clock.GetUtcNow();
        var attempt = await db.LoginAttempts.FromSqlInterpolated($"""
            SELECT * FROM identity.login_attempts WHERE "AccountKey" = {key} FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (attempt is null)
        {
            attempt = new LoginAttempt { AccountKey = key, WindowStartedAtUtc = now };
            db.LoginAttempts.Add(attempt);
        }
        var windowEnds = attempt.WindowStartedAtUtc + RequestWindow;
        if (now >= windowEnds)
        {
            attempt.WindowStartedAtUtc = now;
            attempt.RequestCount = 0;
        }
        else if (attempt.RequestCount >= RequestLimit)
        {
            throw new AuthFault(429, "rate_limit_exceeded", "Too many login requests. Try again later.",
                Math.Max(1, (int)Math.Ceiling((windowEnds - now).TotalSeconds)));
        }
        attempt.RequestCount++;
        if (attempt.LastFailureAtUtc <= now - FailureWindow) ResetFailures(attempt);
        return attempt;
    }

    internal bool RequiresCaptcha(LoginAttempt attempt) => attempt.ConsecutiveFailures >= FailureThreshold;

    internal async Task<AuthFault?> CheckAsync(LoginAttempt attempt, LoginRequest request, CancellationToken ct)
    {
        if (!RequiresCaptcha(attempt)) return null;
        var provided = request.CaptchaChallengeId is not null && !string.IsNullOrWhiteSpace(request.CaptchaCode);
        var now = clock.GetUtcNow();
        var challenge = await FindAsync(attempt, request.Device.InstallationId, ct);
        if (provided && challenge is not null && challenge.ChallengeId == request.CaptchaChallengeId &&
            challenge.ExpiresAtUtc > now && AuthSecurity.Equal(challenge.AnswerHash,
                HashAnswer(challenge, request.CaptchaCode!.Trim().ToUpperInvariant())))
        {
            challenge.ExpiresAtUtc = now;
            return null;
        }

        return new AuthFault(400, provided ? "captcha_invalid" : "captcha_required",
            provided ? "The verification code is incorrect or expired." : "Complete the verification to sign in.")
        { Captcha = Issue(attempt, request.Device.InstallationId, challenge) };
    }

    internal async Task<LoginCaptchaResponse?> RecordFailureAsync(LoginAttempt attempt, Guid installationId, CancellationToken ct)
    {
        attempt.LastFailureAtUtc = clock.GetUtcNow();
        if (attempt.ConsecutiveFailures < int.MaxValue) attempt.ConsecutiveFailures++;
        return RequiresCaptcha(attempt) ? await IssueAsync(attempt, installationId, ct) : null;
    }

    internal async Task<LoginCaptchaResponse> IssueAsync(LoginAttempt attempt, Guid installationId, CancellationToken ct) =>
        Issue(attempt, installationId, await FindAsync(attempt, installationId, ct));

    internal void ResetFailures(LoginAttempt attempt)
    {
        attempt.ConsecutiveFailures = 0;
        attempt.LastFailureAtUtc = null;
    }

    private LoginCaptchaResponse Issue(LoginAttempt attempt, Guid installationId, LoginCaptcha? challenge)
    {
        if (challenge is null)
        {
            challenge = new LoginCaptcha { AccountKey = attempt.AccountKey, InstallationId = installationId };
            db.LoginCaptchas.Add(challenge);
        }
        var image = LoginCaptchaImage.Create(out var answer);
        challenge.ChallengeId = Guid.NewGuid();
        challenge.ExpiresAtUtc = clock.GetUtcNow() + ChallengeLifetime;
        challenge.AnswerHash = HashAnswer(challenge, answer);
        return new LoginCaptchaResponse(challenge.ChallengeId, image, challenge.ExpiresAtUtc);
    }

    private Task<LoginCaptcha?> FindAsync(LoginAttempt attempt, Guid installationId, CancellationToken ct) =>
        db.LoginCaptchas.FromSqlInterpolated($"""
            SELECT * FROM identity.login_captchas WHERE "AccountKey" = {attempt.AccountKey} AND "InstallationId" = {installationId} FOR UPDATE
            """).SingleOrDefaultAsync(ct);

    private string HashAnswer(LoginCaptcha challenge, string answer) => security.Derive(
        $"login-captcha:{challenge.AccountKey}:{challenge.InstallationId:N}:{challenge.ChallengeId:N}:{answer}");
}
