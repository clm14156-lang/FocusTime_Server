using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FocusTime.Api.Modules.FocusSync;
using FocusTime.Api.Modules.FocusSync.Contracts;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Identity.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using static FocusTime.Api.Tests.FocusSyncTests;

namespace FocusTime.Api.Tests;

public sealed class FocusBidirectionalTests
{
    private static SyncPushOperation Delete(Guid id) => new(Guid.NewGuid(), id, SyncOperationKind.Delete, null, null);
    private static async Task<HttpResponseMessage> Request(HttpClient client, TokenResponse token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new("Bearer", token.AccessToken);
        return await client.SendAsync(request);
    }
    private static async Task<SyncChangesResponse> Changes(HttpClient client, TokenResponse token, string? cursor = null, int limit = 50)
    {
        using var response = await Request(client, token, "/api/v1/sync/focus-records/changes?limit=" + limit +
            (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SyncChangesResponse>(FocusPayloadCodec.Json))!;
    }
    private static async Task<PageResponse<FocusRecordChange>> History(HttpClient client, TokenResponse token, string? cursor = null, int limit = 1)
    {
        using var response = await Request(client, token, "/api/v1/focus-records?limit=" + limit +
            (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PageResponse<FocusRecordChange>>(FocusPayloadCodec.Json))!;
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task ChangePagesKeepTheirCommitBoundary_AndLaterDeletesAreReadInTheNextWave()
    {
        await using var database = await TestDatabase.CreateAsync(); await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient(); var user = await SignIn(client, factory);
        var a = Op(); var b = Op(); await Push(client, user, a, b);
        var first = await Changes(client, user, limit: 1); Assert.True(first.HasMore); Assert.Equal(a.RecordId, Assert.Single(first.Changes).RecordId);
        await Push(client, user, Delete(b.RecordId), Op());
        var second = await Changes(client, user, first.NextCursor, 1); Assert.False(second.HasMore);
        Assert.Equal(b.RecordId, Assert.Single(second.Changes).RecordId); Assert.False(second.Changes[0].IsDeleted);
        var later = await Changes(client, user, second.NextCursor); Assert.Equal(new long[] { 3, 4 }, later.Changes.Select(x => x.ServerRevision));
        Assert.True(later.Changes[0].IsDeleted); Assert.Null(later.Changes[0].Record);
        Assert.Empty((await Changes(client, user, later.NextCursor)).Changes);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task HistorySnapshotDoesNotLoseAnUnvisitedRecordDeletedAfterTheFirstPage()
    {
        await using var database = await TestDatabase.CreateAsync(); await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient(); var user = await SignIn(client, factory);
        var a = Op(); var b = Op(); await Push(client, user, a, b);
        var first = await History(client, user); Assert.NotNull(first.NextCursor);
        await Push(client, user, Delete(b.RecordId));
        var second = await History(client, user, first.NextCursor); Assert.Equal(b.RecordId, Assert.Single(second.Items).RecordId); Assert.Null(second.NextCursor);
        var fresh = await History(client, user, limit: 200); Assert.Equal(a.RecordId, Assert.Single(fresh.Items).RecordId);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task DeleteWinsBeforeOrAfterUpload_AndOldReceiptsCannotResurrectHistory()
    {
        await using var database = await TestDatabase.CreateAsync(); await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient(); var user = await SignIn(client, factory);
        var before = Op(); var delete = Delete(before.RecordId);
        Assert.Equal(SyncOperationStatus.Applied, Assert.Single((await Push(client, user, delete)).Results).Status);
        Assert.Equal("record_deleted", Assert.Single((await Push(client, user, before)).Results).Code);
        Assert.Equal(SyncOperationStatus.Duplicate, Assert.Single((await Push(client, user, delete)).Results).Status);
        var after = Op(); await Push(client, user, after);
        var deleted = Assert.Single((await Push(client, user, Delete(after.RecordId) with { BaseRevision = 1 })).Results);
        Assert.Equal(SyncOperationStatus.Applied, deleted.Status); // stale observed revision still deletes immutable history
        Assert.Equal(SyncOperationStatus.Duplicate, Assert.Single((await Push(client, user, after)).Results).Status);
        Assert.Equal("record_deleted", Assert.Single((await Push(client, user, after with { OperationId = Guid.NewGuid() })).Results).Code);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(client, user, after.RecordId)).StatusCode);
        Assert.Equal(3, await db.FocusChanges.CountAsync()); Assert.All(await db.FocusRecords.ToListAsync(), x => Assert.True(x.IsDeleted));
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task CursorsRejectTamperingOtherAccountsAndOtherQueryPurposes_AndRevocationStillApplies()
    {
        await using var database = await TestDatabase.CreateAsync(); await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient(); var a = await SignIn(client, factory); var b = await SignIn(client, factory, "b@example.test");
        await Push(client, a, Op(), Op()); var changes = await Changes(client, a, limit: 1); var history = await History(client, a);
        foreach (var path in new[] {
            "/api/v1/sync/focus-records/changes?cursor=" + changes.NextCursor + "x",
            "/api/v1/sync/focus-records/changes?cursor=" + history.NextCursor,
            "/api/v1/focus-records?cursor=" + changes.NextCursor,
            "/api/v1/sync/focus-records/changes?limit=0" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Request(client, a, path)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(client, b, "/api/v1/sync/focus-records/changes?cursor=" + changes.NextCursor)).StatusCode);
        Assert.Empty((await Changes(client, b)).Changes);
        await client.PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequest(a.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Request(client, a, "/api/v1/sync/focus-records/changes")).StatusCode);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task ByteBoundedPagesMakeProgressWithoutSkippingLargeSnapshots()
    {
        await using var database = await TestDatabase.CreateAsync(); await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient(); var user = await SignIn(client, factory);
        var record = Record() with { CompletedTasks = Enumerable.Range(0, 12).Select(i => new CompletedTaskSnapshot("task" + i, "name", i, null,
            new(new string('a', 16000), []))).ToArray() };
        await Push(client, user, Op(record), Op(record), Op(record));
        string? cursor = null; var revisions = new List<long>();
        for (var page = 0; page < 4; page++)
        {
            var result = await Changes(client, user, cursor, 200);
            Assert.Single(result.Changes); revisions.Add(result.Changes[0].ServerRevision); cursor = result.NextCursor;
            if (!result.HasMore) break;
        }
        Assert.Equal(new long[] { 1, 2, 3 }, revisions);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task ConcurrentWritesAndDeletesProduceGaplessCommitVersions()
    {
        await using var database = await TestDatabase.CreateAsync(); await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient(); var user = await SignIn(client, factory);
        var operations = Enumerable.Range(0, 8).Select(_ => Op()).ToArray();
        await Task.WhenAll(operations.Select(x => Push(client, user, x)));
        await Task.WhenAll(Push(client, user, Delete(operations[0].RecordId)), Push(client, user, Op()));
        var result = await Changes(client, user); Assert.Equal(Enumerable.Range(1, 10).Select(x => (long)x), result.Changes.Select(x => x.ServerRevision));
        Assert.Equal(10, await db.FocusChanges.CountAsync()); Assert.Equal(10, (await db.FocusSyncStates.SingleAsync()).LastRevision);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task UpgradeBackfillsExistingStageFourRecordsWithoutChangingTheirRevision()
    {
        await using var database = await TestDatabase.CreateAsync(); await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync("20261103042017_FocusRecordUpload");
        var user = new AppUser { Email = "legacy@example.test", NormalizedEmail = "LEGACY@EXAMPLE.TEST", UserName = "legacy@example.test", NormalizedUserName = "LEGACY@EXAMPLE.TEST" };
        db.Users.Add(user); var id = Guid.NewGuid(); var payload = JsonSerializer.Serialize(Record(), FocusPayloadCodec.Json);
        db.FocusRecords.Add(new() { UserId = user.Id, RecordId = id, SourceDeviceId = Guid.NewGuid(), Revision = 7, PayloadJson = payload, PayloadHash = AuthSecurity.Hash(payload), ReceivedAtUtc = DateTimeOffset.UtcNow });
        db.FocusSyncStates.Add(new() { UserId = user.Id, LastRevision = 7 }); await db.SaveChangesAsync();
        await db.Database.MigrateAsync(); var backfilled = await db.FocusChanges.SingleAsync();
        Assert.Equal(id, backfilled.RecordId); Assert.Equal(7, backfilled.Revision); Assert.Equal(7, (await db.FocusSyncStates.SingleAsync()).LastRevision);
        await db.Database.MigrateAsync(); Assert.Single(await db.FocusChanges.ToListAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task MixedBatchDeleteValidationAndSameOperationMismatchRemainPerItem()
    {
        await using var database = await TestDatabase.CreateAsync(); await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient(); var user = await SignIn(client, factory);
        var op = Op(); var delete = Delete(op.RecordId);
        var result = (await Push(client, user, delete with { Record = Record() }, op, delete with { OperationId = Guid.NewGuid() })).Results;
        Assert.Equal("delete_record_not_allowed", result[0].Code); Assert.Equal(SyncOperationStatus.Applied, result[1].Status); Assert.Equal(SyncOperationStatus.Applied, result[2].Status);
        Assert.Equal("operation_id_reused", Assert.Single((await Push(client, user, delete)).Results).Code);
        Assert.Equal(2, await db.FocusChanges.CountAsync());
    }
}
