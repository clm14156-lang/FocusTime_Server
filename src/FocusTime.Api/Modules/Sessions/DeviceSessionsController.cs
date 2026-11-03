using System.Security.Claims;
using FocusTime.Api.Modules.Sessions.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FocusTime.Api.Modules.Sessions;

[ApiController]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[EnableRateLimiting("auth")]
public sealed class DeviceSessionsController(DeviceSessionService sessions) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private Guid SessionId => Guid.Parse(User.FindFirstValue("sid")!);

    [HttpGet("api/v1/me/devices")]
    public Task<IReadOnlyList<DeviceResponse>> List(CancellationToken ct) => sessions.ListAsync(UserId, SessionId, ct);

    [HttpDelete("api/v1/me/devices/{deviceId:guid}/sessions")]
    public async Task<IActionResult> RevokeDevice(Guid deviceId, CancellationToken ct)
    {
        await sessions.RevokeAsync(UserId, SessionId, deviceId, null, ct);
        return NoContent();
    }

    [HttpDelete("api/v1/me/sessions/{sessionId:guid}")]
    public async Task<IActionResult> RevokeSession(Guid sessionId, CancellationToken ct)
    {
        await sessions.RevokeAsync(UserId, SessionId, null, sessionId, ct);
        return NoContent();
    }

    [HttpPost("api/v1/auth/logout-all")]
    public async Task<IActionResult> LogoutAll(CancellationToken ct)
    {
        await sessions.RevokeAsync(UserId, SessionId, null, null, ct);
        return NoContent();
    }
}
