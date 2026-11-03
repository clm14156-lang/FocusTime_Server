using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using FocusTime.Api.Modules.FocusSync.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FocusTime.Api.Modules.FocusSync;

[ApiController, Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class FocusSyncController(FocusSyncService sync) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private Guid SessionId => Guid.Parse(User.FindFirstValue("sid")!);
    [HttpPost("api/v1/sync/focus-records/push")]
    public Task<SyncPushResponse> Push(SyncPushRequest request, CancellationToken ct) => sync.PushAsync(UserId, SessionId, request, ct);
    [HttpGet("api/v1/focus-records/{recordId:guid}")]
    public Task<FocusRecordChange> Get(Guid recordId, CancellationToken ct) => sync.GetAsync(UserId, SessionId, recordId, ct);
    [HttpGet("api/v1/sync/focus-records/changes")]
    public Task<SyncChangesResponse> Changes([FromQuery, StringLength(2048)] string? cursor = null,
        [FromQuery, Range(1, 200)] int limit = 50, CancellationToken ct = default) => sync.ChangesAsync(UserId, SessionId, cursor, limit, ct);
    [HttpGet("api/v1/focus-records")]
    public Task<PageResponse<FocusRecordChange>> History([FromQuery, StringLength(2048)] string? cursor = null,
        [FromQuery, Range(1, 200)] int limit = 50, CancellationToken ct = default) => sync.HistoryAsync(UserId, SessionId, cursor, limit, ct);
}
