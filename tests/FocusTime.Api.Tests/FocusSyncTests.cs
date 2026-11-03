using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FocusTime.Api.Modules.FocusSync;
using FocusTime.Api.Modules.FocusSync.Contracts;
using FocusTime.Api.Modules.Identity.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Tests;

public sealed class FocusSyncTests
{
    internal static FocusRecordPayload Record() { var end=DateTimeOffset.UtcNow;return new(1500,1500,end.AddSeconds(-1505),end.AddSeconds(-1500),end,end,
        FocusCompletionKind.Natural,false,"target","目标快照",true,null,null,[new("task","任务快照",0,end,new("说明",[new("子任务",true)]))]); }
    internal static SyncPushOperation Op(FocusRecordPayload? record=null,Guid? recordId=null,Guid? opId=null) =>new(opId??Guid.NewGuid(),recordId??Guid.NewGuid(),SyncOperationKind.Upsert,record??Record(),null);
    internal static async Task<TokenResponse> SignIn(HttpClient client,ApiFactory factory,string email="sync@example.test")
    {
        var challenge=await(await client.PostAsJsonAsync("/api/v1/auth/registration-code",new{email})).Content.ReadFromJsonAsync<EmailChallengeResponse>();
        Assert.Equal(HttpStatusCode.Created,(await client.PostAsJsonAsync("/api/v1/auth/register",new RegisterRequest(email,challenge!.ChallengeId,factory.Mail.Code(email),"SyncTest12345"))).StatusCode);
        var login=await client.PostAsJsonAsync("/api/v1/auth/login",new LoginRequest(email,"SyncTest12345",new(Guid.NewGuid(),"PC","Windows","4")));
        return(await login.Content.ReadFromJsonAsync<TokenResponse>())!;
    }
    internal static async Task<SyncPushResponse> Push(HttpClient client,TokenResponse token,params SyncPushOperation[] operations)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/sync/focus-records/push");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token.AccessToken);
        request.Content=JsonContent.Create(new SyncPushRequest(operations),options:FocusPayloadCodec.Json);
        var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Contains("no-store",response.Headers.CacheControl!.ToString());
        return(await response.Content.ReadFromJsonAsync<SyncPushResponse>(FocusPayloadCodec.Json))!;
    }
    internal static async Task<HttpResponseMessage> Get(HttpClient c,TokenResponse token,Guid id)
    {using var r=new HttpRequestMessage(HttpMethod.Get,"/api/v1/focus-records/"+id);r.Headers.Authorization=new("Bearer",token.AccessToken);return await c.SendAsync(r);}

    [PostgresFact,Trait("Category","PostgreSQL")]
    public async Task Upload_Readback_PreservesSnapshotAndSourceDevice_AndDuplicateDoesNotChangeRevision()
    {
        await using var d=await TestDatabase.CreateAsync();await using var db=d.CreateContext();await db.Database.MigrateAsync();
        await using var f=new ApiFactory(d.ConnectionString);using var c=f.CreateClient();var user=await SignIn(c,f);var op=Op();
        var applied=Assert.Single((await Push(c,user,op)).Results);Assert.Equal(SyncOperationStatus.Applied,applied.Status);Assert.Equal(1,applied.ServerRevision);
        var read=await(await Get(c,user,op.RecordId)).Content.ReadFromJsonAsync<FocusRecordChange>(FocusPayloadCodec.Json);
        Assert.Equal(user.DeviceId,read!.SourceDeviceId);Assert.Equal(op.Record!.ConfiguredSeconds,read.Record!.ConfiguredSeconds);
        Assert.Equal("子任务",read.Record.CompletedTasks[0].Details!.SubTasks[0].Title);
        Assert.Equal(SyncOperationStatus.Duplicate,Assert.Single((await Push(c,user,op)).Results).Status);
        Assert.Equal(SyncOperationStatus.Duplicate,Assert.Single((await Push(c,user,op with{OperationId=Guid.NewGuid()})).Results).Status);
        Assert.Single(await db.FocusRecords.ToListAsync());Assert.Equal(1,(await db.FocusSyncStates.SingleAsync()).LastRevision);
    }

    [PostgresFact,Trait("Category","PostgreSQL")]
    public async Task ConcurrentRetries_AndRepeatedIdsWithinBatch_AreAtomicAndIdempotent()
    {
        await using var d=await TestDatabase.CreateAsync();await using var db=d.CreateContext();await db.Database.MigrateAsync();
        await using var f=new ApiFactory(d.ConnectionString);using var c=f.CreateClient();var user=await SignIn(c,f);var op=Op();
        var responses=await Task.WhenAll(Push(c,user,op,op),Push(c,user,op));
        Assert.Single(responses.SelectMany(x=>x.Results),x=>x.Status==SyncOperationStatus.Applied);
        Assert.Single(await db.FocusRecords.ToListAsync());Assert.Single(await db.FocusSyncReceipts.ToListAsync());
        var other=op with{OperationId=Guid.NewGuid()};
        Assert.All((await Push(c,user,other,other with{OperationId=Guid.NewGuid()})).Results,x=>Assert.Equal(SyncOperationStatus.Duplicate,x.Status));
    }

    [PostgresFact,Trait("Category","PostgreSQL")]
    public async Task MixedBatch_ReturnsPerOperationResults_AndConflictsNeverOverwriteHistory()
    {
        await using var d=await TestDatabase.CreateAsync();await using var db=d.CreateContext();await db.Database.MigrateAsync();
        await using var f=new ApiFactory(d.ConnectionString);using var c=f.CreateClient();var user=await SignIn(c,f);
        var op=Op();await Push(c,user,op);
        var conflict=op with{OperationId=Guid.NewGuid(),Record=op.Record! with{TargetNameSnapshot="变更"}};
        var invalid=Op(Record() with{ActualSeconds=90000});var valid=Op();
        var invalidText=Op(Record() with{TargetNameSnapshot="invalid\0text"});
        var result=(await Push(c,user,conflict,invalid,valid,op with{RecordId=Guid.NewGuid()},invalidText)).Results;
        Assert.Equal(new[]{SyncOperationStatus.Conflict,SyncOperationStatus.Rejected,SyncOperationStatus.Applied,SyncOperationStatus.Conflict,SyncOperationStatus.Rejected},result.Select(x=>x.Status));
        Assert.Equal("operation_id_reused",result[3].Code);
        Assert.Equal("invalid_snapshot_text",result[4].Code);
        Assert.Equal(2,await db.FocusRecords.CountAsync());Assert.Equal(2,(await db.FocusSyncStates.SingleAsync()).LastRevision);
        Assert.Equal("目标快照",(await(await Get(c,user,op.RecordId)).Content.ReadFromJsonAsync<FocusRecordChange>(FocusPayloadCodec.Json))!.Record!.TargetNameSnapshot);
    }

    [PostgresFact,Trait("Category","PostgreSQL")]
    public async Task ScopeIsServerAccount_AndSameRecordIdInDifferentAccountsIsIndependent()
    {
        await using var d=await TestDatabase.CreateAsync();await using var db=d.CreateContext();await db.Database.MigrateAsync();
        await using var f=new ApiFactory(d.ConnectionString);using var c=f.CreateClient();var a=await SignIn(c,f);var b=await SignIn(c,f,"other@example.test");var op=Op();
        await Push(c,a,op);Assert.Equal(HttpStatusCode.NotFound,(await Get(c,b,op.RecordId)).StatusCode);
        Assert.Equal(SyncOperationStatus.Applied,Assert.Single((await Push(c,b,op)).Results).Status);
        Assert.Equal(2,await db.FocusRecords.CountAsync());Assert.All(await db.FocusSyncStates.ToListAsync(),x=>Assert.Equal(1,x.LastRevision));
        Assert.Equal(HttpStatusCode.Unauthorized,(await c.PostAsJsonAsync("/api/v1/sync/focus-records/push",new SyncPushRequest([op]),FocusPayloadCodec.Json)).StatusCode);
    }

    [PostgresFact,Trait("Category","PostgreSQL")]
    public async Task UtcOffsetsAndTaskOrderAreCanonical_WhileChangedOperationPayloadConflicts()
    {
        await using var d=await TestDatabase.CreateAsync();await using var db=d.CreateContext();await db.Database.MigrateAsync();
        await using var f=new ApiFactory(d.ConnectionString);using var c=f.CreateClient();var u=await SignIn(c,f);var record=Record();
        record=record with{CompletedTasks=[record.CompletedTasks[0],new("two","第二项",1,null,null)]};var op=Op(record);await Push(c,u,op);
        var same=record with{CompletedAtUtc=record.CompletedAtUtc.ToOffset(TimeSpan.FromHours(8)),CompletedTasks=record.CompletedTasks.Reverse().ToArray()};
        Assert.Equal(SyncOperationStatus.Duplicate,Assert.Single((await Push(c,u,op with{Record=same})).Results).Status);
        Assert.Equal(SyncOperationStatus.Conflict,Assert.Single((await Push(c,u,op with{Record=record with{BlockingEnabled=false}})).Results).Status);
    }

    [PostgresFact,Trait("Category","PostgreSQL")]
    public async Task PreparationEnd_ValidatesWithoutDiscardingPlannedFutureStart_AndDeleteCreatesTombstone()
    {
        await using var d=await TestDatabase.CreateAsync();await using var db=d.CreateContext();await db.Database.MigrateAsync();
        await using var f=new ApiFactory(d.ConnectionString);using var c=f.CreateClient();var u=await SignIn(c,f);var now=DateTimeOffset.UtcNow;
        var early=Record() with{PreparationStartedAtUtc=now,FocusStartedAtUtc=now.AddSeconds(5),PlannedEndAtUtc=now.AddSeconds(1505),CompletedAtUtc=now.AddSeconds(1),ActualSeconds=0,IsForcedMode=true,CompletionKind=FocusCompletionKind.EarlyEnd};
        var op=Op(early);var result=(await Push(c,u,op,Op(early with{IsForcedMode=false}),op with{OperationId=Guid.NewGuid(),Kind=SyncOperationKind.Delete,Record=null})).Results;
        Assert.Equal(SyncOperationStatus.Applied,result[0].Status);Assert.Equal("invalid_time_range",result[1].Code);Assert.Equal(SyncOperationStatus.Applied,result[2].Status);
        Assert.Equal(HttpStatusCode.NotFound,(await Get(c,u,op.RecordId)).StatusCode);
    }

    [PostgresFact,Trait("Category","PostgreSQL")]
    public async Task RevokedSessionCannotUpload_AndInvalidIdsAndOversizedSnapshotsAreRejected()
    {
        await using var d=await TestDatabase.CreateAsync();await using var db=d.CreateContext();await db.Database.MigrateAsync();
        await using var f=new ApiFactory(d.ConnectionString);using var c=f.CreateClient();var u=await SignIn(c,f);
        var result=(await Push(c,u,Op() with{RecordId=Guid.Empty},Op(Record() with{TargetNameSnapshot=new string('a',4097)}))).Results;
        Assert.All(result,x=>Assert.Equal(SyncOperationStatus.Rejected,x.Status));Assert.Empty(await db.FocusRecords.ToListAsync());
        await c.PostAsJsonAsync("/api/v1/auth/logout",new LogoutRequest(u.RefreshToken));
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/v1/sync/focus-records/push"){Content=JsonContent.Create(new SyncPushRequest([Op()]),options:FocusPayloadCodec.Json)};
        request.Headers.Authorization=new("Bearer",u.AccessToken);Assert.Equal(HttpStatusCode.Unauthorized,(await c.SendAsync(request)).StatusCode);
    }
}
