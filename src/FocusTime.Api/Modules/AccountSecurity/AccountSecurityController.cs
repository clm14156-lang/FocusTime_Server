using System.ComponentModel.DataAnnotations;
using FocusTime.Api.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FocusTime.Api.Modules.AccountSecurity;

public sealed record VerifySecurityCode(Guid ChallengeId,
    [param: Required, RegularExpression("^[0-9]{6}$")] string Code);
public sealed record SecurityProof(string Ticket, DateTimeOffset ExpiresAtUtc);
public sealed record NewEmailCode([param: Required, MaxLength(64)] string Ticket,
    [param: Required, EmailAddress, MaxLength(256)] string NewEmail);
public sealed record CompleteEmailChange([param: Required, MaxLength(64)] string Ticket, Guid ChallengeId,
    [param: Required, RegularExpression("^[0-9]{6}$")] string Code);
public sealed record ChangeLoginPassword([param: Required, MaxLength(128)] string CurrentPassword,
    [param: Required, MinLength(8), MaxLength(128)] string NewPassword);
public sealed record CloseAccount([param: Required, MaxLength(64)] string Ticket,
    [param: Required, RegularExpression("^CLOSE$")] string Confirmation);
public sealed record SecuritySessionResponse(TokenResponse Session, string NotificationStatus = "queued");

[ApiController, Authorize, Route("api/v1/me/security")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[EnableRateLimiting("auth")]
public sealed class AccountSecurityController(AccountSecurityService security) : ControllerBase
{
    private string Authorization => Request.Headers.Authorization.ToString();
    [HttpPost("email/current/code")]
    public Task<EmailChallengeResponse> CurrentEmailCode(CancellationToken ct) =>
        security.SendCurrentCodeAsync(User, Authorization, "email-current", ct);
    [HttpPost("email/current/verify")]
    public Task<SecurityProof> VerifyCurrentEmail(VerifySecurityCode request, CancellationToken ct) =>
        security.VerifyAsync(User, Authorization, "email-current", request, ct);
    [HttpPost("email/new/code")]
    public Task<EmailChallengeResponse> NewEmailCode(NewEmailCode request, CancellationToken ct) =>
        security.SendNewEmailCodeAsync(User, Authorization, request, ct);
    [HttpPost("email/complete")]
    public Task<SecuritySessionResponse> CompleteEmail(CompleteEmailChange request, CancellationToken ct) =>
        security.ChangeEmailAsync(User, Authorization, request, ct);
    [HttpPost("password")]
    public Task<SecuritySessionResponse> Password(ChangeLoginPassword request, CancellationToken ct) =>
        security.ChangePasswordAsync(User, Authorization, request, ct);
    [HttpPost("closure/code")]
    public Task<EmailChallengeResponse> ClosureCode(CancellationToken ct) =>
        security.SendCurrentCodeAsync(User, Authorization, "closure", ct);
    [HttpPost("closure/verify")]
    public Task<SecurityProof> VerifyClosure(VerifySecurityCode request, CancellationToken ct) =>
        security.VerifyAsync(User, Authorization, "closure", request, ct);
    [HttpPost("closure/complete")]
    public async Task<IActionResult> CompleteClosure(CloseAccount request, CancellationToken ct)
    {
        await security.CloseAsync(User, Authorization, request, ct);
        return NoContent();
    }
}
