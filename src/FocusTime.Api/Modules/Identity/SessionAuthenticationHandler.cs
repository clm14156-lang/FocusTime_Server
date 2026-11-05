using System.Security.Claims;
using System.Text.Encodings.Web;
using FocusTime.Api.Common;
using FocusTime.Api.Modules.Admin;
using FocusTime.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;

namespace FocusTime.Api.Modules.Identity;

public sealed class SessionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, AppDbContext db, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "SessionBearer";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return AuthenticateResult.NoResult();
        var header = Request.Headers.Authorization.ToString();
        string? token = null;
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = header[7..].Trim();
        else if (header.Length == 0 && Request.Path.StartsWithSegments(AdminAccess.Path))
            token = Request.Cookies[AdminAccess.CookieName];
        if (token is null) return AuthenticateResult.NoResult();
        if (token.Length != 64) return AuthenticateResult.Fail("Invalid bearer credential.");
        var hash = AuthSecurity.Hash(token);
        var now = clock.GetUtcNow();
        var session = await (from item in db.LoginSessions.AsNoTracking()
            join user in db.Users.AsNoTracking() on item.UserId equals user.Id
            where item.AccessHash == hash && item.RevokedAtUtc == null && item.AccessExpiresAtUtc > now &&
                item.ExpiresAtUtc > now && item.SecurityStamp == user.SecurityStamp
            select item).SingleOrDefaultAsync(Context.RequestAborted);
        if (session is null) return AuthenticateResult.Fail("Invalid or expired session.");
        // Throttle activity writes; a concurrent revocation can never be overwritten here.
        var seen = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        var touched = await db.LoginSessions.Where(x => x.Id == session.Id && x.AccessHash == hash && x.RevokedAtUtc == null &&
            x.LastSeenAtUtc < seen.AddMinutes(-1))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.LastSeenAtUtc, seen), Context.RequestAborted);
        if (touched > 0)
            await db.LoginDevices.Where(x => x.Id == session.DeviceId && x.LastSeenAtUtc < seen)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.LastSeenAtUtc, seen), Context.RequestAborted);
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString()),
            new Claim("sid", session.Id.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        Response.Headers.CacheControl = "no-store";
        return ApiProblems.WriteAsync(Context, 401, "Please sign in again.");
    }
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => ApiProblems.WriteAsync(Context, 403);
}
