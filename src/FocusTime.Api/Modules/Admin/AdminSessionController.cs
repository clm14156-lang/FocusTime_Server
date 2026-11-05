using System.Security.Claims;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Identity.Contracts;
using FocusTime.Api.Modules.Sessions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;

namespace FocusTime.Api.Modules.Admin;

[ApiController]
[Route("api/v1/admin/session")]
[Authorize(Policy = AdminAccess.Policy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminSessionController(AccountService accounts, AdminAccess admins,
    AppDbContext db, DeviceSessionService sessions, IWebHostEnvironment environment, UserManager<AppUser> users, TimeProvider clock) : ControllerBase
{
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<AdminSessionProfile>> Login(LoginRequest request, CancellationToken ct)
    {
        if (!IsBrowserMutation()) return StatusCode(StatusCodes.Status403Forbidden);
        var result = await accounts.LoginAdministratorAsync(request, admins.Allows, ct);
        var cookie = SessionCookie();
        cookie.Expires = result.ExpiresAtUtc;
        Response.Cookies.Append(AdminAccess.CookieName, result.AccessToken, cookie);
        return new AdminSessionProfile(result.User.Email, result.ExpiresAtUtc);
    }

    [HttpGet]
    public async Task<ActionResult<AdminSessionProfile>> Get(CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var sessionId = Guid.Parse(User.FindFirstValue("sid")!);
        var result = await (from session in db.LoginSessions.AsNoTracking()
            join user in db.Users.AsNoTracking() on session.UserId equals user.Id
            where session.Id == sessionId && user.Id == userId
            select new AdminSessionProfile(user.Email!, session.AccessExpiresAtUtc)).SingleOrDefaultAsync(ct);
        if (result is null) return Unauthorized();
        return result;
    }

    [HttpDelete]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (!IsBrowserMutation()) return StatusCode(StatusCodes.Status403Forbidden);
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var sessionId = Guid.Parse(User.FindFirstValue("sid")!);
        await sessions.RevokeAsync(userId, sessionId, null, sessionId, ct);
        Response.Cookies.Delete(AdminAccess.CookieName, SessionCookie());
        return NoContent();
    }

    [HttpPost("/api/v1/admin/password")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangePassword(AdminPasswordRequest request, CancellationToken ct)
    {
        if (!IsBrowserMutation()) return StatusCode(StatusCodes.Status403Forbidden);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.Length == 0) authorization = "Bearer " + Request.Cookies[AdminAccess.CookieName];
        // Recheck under the same account lock used by login, logout and password recovery.
        var session = await AccountTransactions.LockSessionAsync(db, clock, User, authorization, ct);
        var user = await users.FindByIdAsync(session.UserId.ToString())
            ?? throw new AuthFault(401, "session_expired", "Please sign in again.");
        var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());

        async Task Fail(string code, int status, string message)
        {
            db.AdminSecurityAudits.Add(new()
            {
                ActorUserId = user.Id, OccurredAtUtc = now, Result = "failure", FailureCode = code
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            throw new AuthFault(status, code, message);
        }

        if (await users.IsLockedOutAsync(user))
            await Fail("account_locked", 429, "Too many failed attempts. Try again later.");
        if (!await users.CheckPasswordAsync(user, request.CurrentPassword))
        {
            var counted = await users.AccessFailedAsync(user);
            if (!counted.Succeeded) throw new InvalidOperationException("Unable to update account attempt count.");
            if (await users.IsLockedOutAsync(user))
                await Fail("account_locked", 429, "Too many failed attempts. Try again later.");
            await Fail("current_password_incorrect", 400, "The current password is incorrect.");
        }
        if (request.CurrentPassword == request.NewPassword)
            await Fail("password_unchanged", 400, "The new password must differ from the current password.");

        // Identity validates policy, hashes the password and rotates the security stamp.
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            await Fail("password_policy_failed", 400, "Use 8–128 characters with at least one letter and one digit.");
        if (!(await users.ResetAccessFailedCountAsync(user)).Succeeded)
            throw new InvalidOperationException("Unable to reset account attempt count.");
        var email = user.NormalizedEmail!;
        await db.EmailChallenges.Where(x => x.Email == email && x.Purpose == "reset")
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Consumed, true).SetProperty(x => x.TicketConsumed, true), ct);
        await db.LoginSessions.Where(x => x.UserId == user.Id && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAtUtc, now), ct);
        db.AdminSecurityAudits.Add(new() { ActorUserId = user.Id, OccurredAtUtc = now, Result = "success" });
        // Failure to persist the audit rolls back both the password and session changes.
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        Response.Cookies.Delete(AdminAccess.CookieName, SessionCookie());
        return NoContent();
    }


    // A cross-origin page cannot set this header because admin endpoints do not allow CORS.
    private bool IsBrowserMutation() => Request.Headers["X-FocusTime-Admin"] == "1";

    private CookieOptions SessionCookie() => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps || !environment.IsDevelopment() || !IsLoopbackHost(),
        SameSite = SameSiteMode.Strict,
        Path = AdminAccess.Path
    };

    private bool IsLoopbackHost()
    {
        var host = Request.Host.Host;
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var address) && System.Net.IPAddress.IsLoopback(address));
    }
}

public sealed record AdminSessionProfile(string Email, DateTimeOffset ExpiresAtUtc);
