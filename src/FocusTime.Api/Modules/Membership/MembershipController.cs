using System.ComponentModel.DataAnnotations;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Membership.Contracts;
using FocusTime.Api.Modules.Sessions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Membership;

public sealed record EntitlementLeaseRequest(
    [param: Required, RegularExpression("^[a-f0-9]{64}$")] string ApiScope,
    [param: Required, RegularExpression("^[a-f0-9]{64}$")] string MachineBinding,
    [param: Required, RegularExpression("^[a-f0-9]{64}$")] string Nonce);

[ApiController, Authorize, Route("api/v1"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MembershipController(AppDbContext db, MembershipService memberships, EntitlementSigner signer, TimeProvider clock) : ControllerBase
{
    [AllowAnonymous, HttpGet("entitlement-keys")]
    public object Keys() => new { issuer = "FocusTime_Server", keys = new[] { signer.PublicKey } };

    [HttpGet("me/membership")]
    public async Task<MembershipResponse> Get(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var session = await LockSessionAsync(ct);
        return await memberships.GetAsync(session.UserId, ct);
    }

    [HttpPost("me/entitlement-lease")]
    public async Task<EntitlementLeaseResponse> Lease(EntitlementLeaseRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var session = await LockSessionAsync(ct);
        var device = await db.LoginDevices.AsNoTracking().SingleAsync(x => x.Id == session.DeviceId && x.UserId == session.UserId, ct);
        var status = await memberships.GetAsync(session.UserId, ct);
        var now = clock.GetUtcNow();
        var expires = new[] { now.AddHours(72), session.ExpiresAtUtc, status.ExpiresAtUtc ?? DateTimeOffset.MaxValue }.Min();
        var token = signer.Sign(new
        {
            v = 1, iss = "FocusTime_Server", aud = "focusapp-service", sub = session.UserId.ToString("D"),
            deviceId = device.Id.ToString("D"), sessionId = session.Id.ToString("D"), installationId = device.InstallationId.ToString("D"),
            apiScope = request.ApiScope, machineBinding = request.MachineBinding, nonce = request.Nonce,
            kind = status.Kind.ToString().ToLowerInvariant(), rights = status.Entitlements, version = status.Version,
            iat = now.ToUnixTimeSeconds(), exp = expires.ToUnixTimeSeconds(),
            sessionExp = session.ExpiresAtUtc.ToUnixTimeSeconds(), grantExp = status.ExpiresAtUtc?.ToUnixTimeSeconds()
        });
        return new(token, expires, signer.PublicKey.KeyId);
    }

    private Task<LoginSession> LockSessionAsync(CancellationToken ct) =>
        AccountTransactions.LockSessionAsync(db, clock, User, Request.Headers.Authorization.ToString(), ct);
}
