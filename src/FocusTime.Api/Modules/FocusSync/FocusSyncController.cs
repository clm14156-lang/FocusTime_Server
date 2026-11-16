using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using FocusTime.Api.Modules.FocusSync.Contracts;
using FocusTime.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.FocusSync;

[ApiController, Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class FocusSyncController(FocusSyncService sync) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private Guid SessionId => Guid.Parse(User.FindFirstValue("sid")!);
    [HttpGet("api/v1/sync/versions")]
    public async Task<SyncVersions> Versions([FromServices] AppDbContext db, CancellationToken ct)
    {
        var userId = UserId;
        // This authenticated read is a single PostgreSQL snapshot. Actual writes still take the account lock.
        return await db.Database.SqlQuery<SyncVersions>($"""
            SELECT COALESCE((SELECT "LastRevision" FROM focus.sync_states WHERE "UserId"={userId}), 0) AS "Focus",
                   COALESCE((SELECT revision FROM focus.website_rule_sets WHERE user_id={userId}), 0) AS "Websites"
            """).SingleAsync(ct);
    }

    public sealed record SyncVersions(long Focus, long Websites);
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
