using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FocusTime.Api.Modules.AccountSecurity;
using FocusTime.Api.Modules.Identity.Contracts;
using FocusTime.Api.Modules.FocusSync;
using FocusTime.Api.Modules.Membership;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FocusTime.Api.Tests;

public sealed class AccountSecurityTests
{
    private const string Password = "Original12345";
    private const string NewPassword = "Changed12345";
    private static async Task<TokenResponse> Register(HttpClient client, ApiFactory factory, string email)
    {
        var c = await (await client.PostAsJsonAsync("/api/v1/auth/registration-code", new { email })).Content.ReadFromJsonAsync<EmailChallengeResponse>();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, c!.ChallengeId,
            factory.Mail.Code(email), Password, new(Guid.NewGuid(), "Current browser", "Web", "1.0")));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }
    private static async Task<TokenResponse> Login(HttpClient client, string email, string password = Password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password,
            new(Guid.NewGuid(), "Other computer", "Windows", "2.0")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }
    private static Task<HttpResponseMessage> Send(HttpClient client, TokenResponse token, string path, object? body = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? (body is null ? HttpMethod.Get : HttpMethod.Post), "/api/v1/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }
    private static async Task<T> Post<T>(HttpClient client, TokenResponse token, string path, object body)
    {
        var response = await Send(client, token, "me/security/" + path, body);
        Assert.True(response.IsSuccessStatusCode, response.StatusCode + " " + await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static async Task<SecurityProof> Proof(HttpClient client, ApiFactory factory, TokenResponse token, string purpose)
    {
        var prefix = purpose == "closure" ? "closure" : "email/current";
        var c = await Post<EmailChallengeResponse>(client, token, prefix + "/code", new { });
        return await Post<SecurityProof>(client, token, prefix + "/verify", new { c.ChallengeId, code = factory.Mail.Code(token.User.Email, purpose) });
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Password_RotatesCurrentCredential_RevokesOthers_QueuesNotice_AndAuditsFailures()
    {
        await using var database = await TestDatabase.CreateAsync(); await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient();
        var a = await Register(client, factory, "password@example.test"); var b = await Login(client, a.User.Email);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, a, "me/security/password", new { currentPassword = "Wrong12345", newPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, a, "auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, a, "me/security/password", new { currentPassword = Password, newPassword = "abcdefgh" })).StatusCode);
        factory.Mail.Fail = true; // Notification delivery failure must not undo a password change.
        var changed = await Post<SecuritySessionResponse>(client, a, "password", new { currentPassword = Password, newPassword = NewPassword });
        Assert.Equal("queued", changed.NotificationStatus); Assert.Equal(a.SessionId, changed.Session.SessionId);
        Assert.Equal(a.DeviceId, changed.Session.DeviceId); Assert.NotEqual(a.AccessToken, changed.Session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, changed.Session, "auth/me")).StatusCode);
        foreach (var old in new[] { a, b })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, old, "auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(old.RefreshToken, Guid.NewGuid()))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(a.User.Email, Password, new(Guid.NewGuid(),"PC","Web","1")))).StatusCode);
        await Login(client, a.User.Email, NewPassword);
        Assert.Equal(1, await db.SecurityNotifications.CountAsync(x => x.UserId == a.User.Id));
        await using (var scope = factory.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<SecurityNotificationDispatcher>().DispatchOneAsync(default);
        Assert.False((await db.SecurityNotifications.AsNoTracking().SingleAsync()).Delivered);
        factory.Mail.Fail = false; factory.Clock.Advance(TimeSpan.FromMinutes(3));
        await using (var scope = factory.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<SecurityNotificationDispatcher>().DispatchOneAsync(default);
        Assert.Contains(factory.Mail.Notices, x => x.Contains("密码修改通知"));
        var notice = await db.SecurityNotifications.AsNoTracking().SingleAsync(); Assert.True(notice.Delivered); Assert.Equal("", notice.Recipient);
        Assert.Equal(2, await db.AccountSecurityAudits.CountAsync(x => x.Operation == "password.change" && x.Result == "failed"));
        Assert.Equal(1, await db.AccountSecurityAudits.CountAsync(x => x.Operation == "password.change" && x.Result == "success"));
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Email_RequiresBothAddresses_AndSessionBoundProof_RejectsDuplicates_RevokesOldCredentials()
    {
        await using var database = await TestDatabase.CreateAsync(); await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient();
        var a = await Register(client,factory,"old@example.test"); var b = await Login(client,a.User.Email);
        var taken = await Register(client,factory,"taken@example.test"); factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var proof = await Proof(client,factory,a,"email-current");
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client,b,"me/security/email/new/code",new { proof.Ticket,newEmail="new@example.test" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(client,a,"me/security/email/new/code",new { proof.Ticket,newEmail=taken.User.Email })).StatusCode);
        var c = await Post<EmailChallengeResponse>(client,a,"email/new/code",new { proof.Ticket,newEmail="new@example.test" });
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(client,a,"me/security/email/new/code",new { proof.Ticket,newEmail="new@example.test" })).StatusCode);
        var code=factory.Mail.Code("new@example.test","email-new");
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client,a,"me/security/email/complete",new { proof.Ticket,c.ChallengeId,code=code=="000000"?"111111":"000000" })).StatusCode);
        var changed=await Post<SecuritySessionResponse>(client,a,"email/complete",new { proof.Ticket,c.ChallengeId,code });
        Assert.Equal("new@example.test",changed.Session.User.Email); Assert.Equal(a.SessionId,changed.Session.SessionId);
        Assert.Equal(HttpStatusCode.OK,(await Send(client,changed.Session,"auth/me")).StatusCode);
        foreach(var token in new[] { a,b }) Assert.Equal(HttpStatusCode.Unauthorized,(await Send(client,token,"auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(client,changed.Session,"me/security/email/complete",new { proof.Ticket,c.ChallengeId,code })).StatusCode);
        Assert.Equal(2,await db.SecurityNotifications.CountAsync(x=>x.UserId==a.User.Id));
        var challenge=await db.SecurityChallenges.AsNoTracking().SingleAsync(x=>x.Id==c.ChallengeId);
        Assert.True(challenge.ProofConsumed); Assert.True(challenge.Consumed); Assert.NotEqual(code,challenge.CodeHash);
        await Login(client,"new@example.test");
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Codes_CapAttempts_Expire_AndCannotCrossOperationOrAccount()
    {
        await using var database = await TestDatabase.CreateAsync(); await using var db=database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory=new ApiFactory(database.ConnectionString); using var client=factory.CreateClient();
        var a=await Register(client,factory,"codes@example.test"); var b=await Register(client,factory,"foreign@example.test");
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var c=await Post<EmailChallengeResponse>(client,a,"closure/code",new{}); var code=factory.Mail.Code(a.User.Email,"closure");
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(client,b,"me/security/closure/verify",new{c.ChallengeId,code})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(client,a,"me/security/email/current/verify",new{c.ChallengeId,code})).StatusCode);
        for(var i=0;i<5;i++) Assert.Equal(HttpStatusCode.BadRequest,(await Send(client,a,"me/security/closure/verify",new{c.ChallengeId,code=code=="000000"?"111111":"000000"})).StatusCode);
        Assert.Equal(5,(await db.SecurityChallenges.AsNoTracking().SingleAsync(x=>x.Id==c.ChallengeId)).Attempts);
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(client,a,"me/security/closure/verify",new{c.ChallengeId,code})).StatusCode);
        factory.Clock.Advance(TimeSpan.FromSeconds(61)); c=await Post<EmailChallengeResponse>(client,a,"closure/code",new{});
        factory.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(client,a,"me/security/closure/verify",new{c.ChallengeId,code=factory.Mail.Code(a.User.Email,"closure")})).StatusCode);
        factory.Mail.Fail=true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await Send(client,a,"me/security/closure/code",new{})).StatusCode);
        factory.Mail.Fail=false;
        Assert.Equal(HttpStatusCode.TooManyRequests,(await Send(client,a,"me/security/closure/code",new{})).StatusCode);
        Assert.True((await db.SecurityChallenges.AsNoTracking().OrderByDescending(x=>x.CreatedAtUtc).FirstAsync()).Consumed);
        Assert.Equal(1,await db.AccountSecurityAudits.CountAsync(x=>x.Operation=="closure.code"&&x.FailureCode=="email_unavailable"));
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task SoftClosure_RevokesAllSessionsAndMembership_RetainsCloud_AndBlocksRecovery()
    {
        await using var database=await TestDatabase.CreateAsync(); await using var db=database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory=new ApiFactory(database.ConnectionString); using var client=factory.CreateClient();
        var a=await Register(client,factory,"close@example.test"); var b=await Login(client,a.User.Email); var now=factory.Clock.GetUtcNow();
        db.FocusRecords.Add(new FocusRecord { UserId=a.User.Id,RecordId=Guid.NewGuid(),SourceDeviceId=a.DeviceId,Revision=1,PayloadHash=new string('a',64),ReceivedAtUtc=now,PayloadJson="{\"retained\":true}" });
        db.MembershipGrants.Add(new MembershipGrant { Id=Guid.NewGuid(),UserId=a.User.Id,Kind="lifetime",StartsAtUtc=now,CreatedAtUtc=now });
        await db.SaveChangesAsync(); factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var proof=await Proof(client,factory,a,"closure");
        Assert.Equal(HttpStatusCode.BadRequest,(await Send(client,a,"me/security/closure/complete",new{proof.Ticket,confirmation=""})).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await Send(client,a,"me/security/closure/complete",new{proof.Ticket,confirmation="CLOSE"})).StatusCode);
        foreach(var old in new[]{a,b})
        {
            Assert.Equal(HttpStatusCode.Unauthorized,(await Send(client,old,"auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/api/v1/auth/refresh",new RefreshRequest(old.RefreshToken,Guid.NewGuid()))).StatusCode);
        }
        db.ChangeTracker.Clear();
        var user=await db.Users.SingleAsync(x=>x.Id==a.User.Id); Assert.NotNull(user.ClosedAtUtc); Assert.Null(user.PasswordHash);
        Assert.Equal("{\"retained\": true}",(await db.FocusRecords.SingleAsync()).PayloadJson);
        Assert.NotNull((await db.MembershipGrants.SingleAsync()).RevokedAtUtc);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/api/v1/auth/login",new LoginRequest(a.User.Email,Password,new(Guid.NewGuid(),"PC","Web","1")))).StatusCode);
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var reset=await (await client.PostAsJsonAsync("/api/v1/auth/password-reset/code",new{email=a.User.Email})).Content.ReadFromJsonAsync<EmailChallengeResponse>();
        Assert.NotNull(reset); Assert.False(factory.Mail.Codes.ContainsKey(a.User.Email.ToUpperInvariant()+":reset"));
        Assert.Equal(1,await db.AccountSecurityAudits.CountAsync(x=>x.Operation=="account.close"&&x.Result=="success"));
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task LogoutOthers_PreservesCurrentDevice_RejectsForeignDevice_AndAudits()
    {
        await using var database=await TestDatabase.CreateAsync(); await using var db=database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory=new ApiFactory(database.ConnectionString); using var client=factory.CreateClient();
        var a=await Register(client,factory,"devices2@example.test"); var b=await Login(client,a.User.Email); var other=await Register(client,factory,"foreign2@example.test");
        Assert.Equal(HttpStatusCode.NotFound,(await Send(client,a,$"me/devices/{other.DeviceId}/sessions",method:HttpMethod.Delete)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await Send(client,a,"me/devices/logout-others",new{})).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Send(client,a,"auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await Send(client,b,"auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Send(client,other,"auth/me")).StatusCode);
        Assert.Equal(2,await db.AccountSecurityAudits.CountAsync(x=>x.UserId==a.User.Id));
    }
}
