using System.ComponentModel.DataAnnotations;
using FocusTime.Api.Modules.Sessions.Contracts;

namespace FocusTime.Api.Modules.Identity.Contracts;

public sealed record EmailCodeRequest([param: Required, EmailAddress, MaxLength(256)] string Email);
public sealed record EmailChallengeResponse(Guid ChallengeId, DateTimeOffset ExpiresAtUtc, int ResendAfterSeconds);
public sealed record RegisterRequest(
    [param: Required, EmailAddress, MaxLength(256)] string Email,
    Guid ChallengeId,
    [param: Required, RegularExpression("^[0-9]{6}$")] string Code,
    [param: Required, MinLength(8), MaxLength(128)] string Password);
public sealed record LoginRequest(
    [param: Required, EmailAddress, MaxLength(256)] string Email,
    [param: Required, MaxLength(128)] string Password,
    [param: Required] DeviceRegistration Device);
public sealed record RefreshRequest([param: Required, MaxLength(512)] string RefreshToken, Guid RequestId);
public sealed record LogoutRequest([param: Required, MaxLength(512)] string RefreshToken);
public sealed record UserProfile(Guid Id, string Email, bool EmailConfirmed)
{
    public string Membership { get; init; } = "normal";
}
public sealed record TokenResponse(string TokenType, string AccessToken, DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken, DateTimeOffset RefreshTokenExpiresAtUtc, Guid SessionId, Guid DeviceId, UserProfile User);
public sealed record VerifyPasswordResetRequest(Guid ChallengeId,
    [param: Required, RegularExpression("^[0-9]{6}$")] string Code);
public sealed record PasswordResetTicket(string ResetTicket, DateTimeOffset ExpiresAtUtc);
public sealed record CompletePasswordResetRequest(
    [param: Required, MaxLength(512)] string ResetTicket,
    [param: Required, MinLength(8), MaxLength(128)] string NewPassword);
