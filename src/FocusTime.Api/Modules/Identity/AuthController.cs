using System.Security.Claims;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using FocusTime.Api.Modules.Membership;

namespace FocusTime.Api.Modules.Identity;

[ApiController]
[Route("api/v1/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[EnableRateLimiting("auth")]
public sealed class AuthController(AccountService accounts, AppDbContext db, MembershipService memberships) : ControllerBase
{
    [HttpPost("registration-code")]
    public Task<EmailChallengeResponse> RegistrationCode(EmailCodeRequest request, CancellationToken ct) =>
        accounts.SendCodeAsync(request.Email, "register", ct);

    [HttpPost("register")]
    public async Task<ActionResult<UserProfile>> Register(RegisterRequest request, CancellationToken ct) =>
        StatusCode(201, await memberships.ApplyAsync(await accounts.RegisterAsync(request, ct), ct));

    [HttpPost("login")]
    public async Task<TokenResponse> Login(LoginRequest request, CancellationToken ct)
    {
        var response = await accounts.LoginAsync(request, ct);
        return response with { User = await memberships.ApplyAsync(response.User, ct) };
    }

    [HttpPost("refresh")]
    public async Task<TokenResponse> Refresh(RefreshRequest request, CancellationToken ct)
    {
        var response = await accounts.RefreshAsync(request, ct);
        return response with { User = await memberships.ApplyAsync(response.User, ct) };
    }

    // Possession of a refresh credential only grants revocation of its own session here.
    // This also allows durable logout retries after an access token has expired.
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken ct)
    {
        await accounts.LogoutAsync(request.RefreshToken, ct);
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<UserProfile> Me(CancellationToken ct)
    {
        var id = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        return await memberships.ApplyAsync(new(user.Id, user.Email!, user.EmailConfirmed), ct);
    }

    [HttpPost("password-reset/code")]
    public Task<EmailChallengeResponse> ResetCode(EmailCodeRequest request, CancellationToken ct) =>
        accounts.SendCodeAsync(request.Email, "reset", ct);

    [HttpPost("password-reset/verify")]
    public Task<PasswordResetTicket> VerifyReset(VerifyPasswordResetRequest request, CancellationToken ct) =>
        accounts.VerifyResetAsync(request, ct);

    [HttpPost("password-reset/complete")]
    public async Task<IActionResult> CompleteReset(CompletePasswordResetRequest request, CancellationToken ct)
    {
        await accounts.CompleteResetAsync(request, ct);
        return NoContent();
    }
}
