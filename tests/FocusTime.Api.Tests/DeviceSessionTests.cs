using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Identity.Contracts;
using FocusTime.Api.Modules.Sessions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FocusTime.Api.Tests;

public sealed class DeviceSessionTests
{
    private const string Email = "devices@example.test";
    private const string Password = "Devices12345";
    private static async Task Register(HttpClient client, ApiFactory factory, string email = Email)
    {
        var code = await (await client.PostAsJsonAsync("/api/v1/auth/registration-code", new { email }))
            .Content.ReadFromJsonAsync<EmailChallengeResponse>();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, code!.ChallengeId, factory.Mail.Code(email), Password))).StatusCode);
    }
    private static async Task<TokenResponse> Login(HttpClient client, Guid? installation = null, string email = Email)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password,
            new(installation ?? Guid.NewGuid(), "Office PC", "Windows", "3.0")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }
    private static async Task<HttpResponseMessage> Send(HttpClient client, TokenResponse token, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return await client.SendAsync(request);
    }
    private static async Task<DeviceResponse[]> Devices(HttpClient client, TokenResponse token)
    {
        var response = await Send(client, token, HttpMethod.Get, "/api/v1/me/devices");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        return (await response.Content.ReadFromJsonAsync<DeviceResponse[]>())!;
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task List_ShowsOwnedActiveDevices_CurrentSession_AndServerActivity()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient();
        await Register(client, factory); await Register(client, factory, "other@example.test");
        var a = await Login(client); var b = await Login(client); await Login(client, email: "other@example.test");
        var rows = await Devices(client, a);
        Assert.Equal(2, rows.Length);
        Assert.Equal(a.DeviceId, rows[0].Id); Assert.True(rows[0].IsCurrentDevice);
        Assert.True(Assert.Single(rows[0].Sessions).IsCurrentSession);
        Assert.False(rows.Single(x => x.Id == b.DeviceId).IsCurrentDevice);
        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new RefreshRequest(b.RefreshToken, Guid.NewGuid()))).StatusCode);
        rows = await Devices(client, a);
        Assert.True(rows[0].LastSeenAtUtc > rows[0].FirstSeenAtUtc);
        Assert.Equal(rows[0].LastSeenAtUtc, rows[0].Sessions[0].LastSeenAtUtc);
        Assert.True(rows.Single(x => x.Id == b.DeviceId).Sessions[0].LastSeenAtUtc > rows.Single(x => x.Id == b.DeviceId).FirstSeenAtUtc);
        await db.LoginSessions.Where(x => x.Id == b.SessionId).ExecuteUpdateAsync(set =>
            set.SetProperty(x => x.ExpiresAtUtc, factory.Clock.GetUtcNow().AddSeconds(-1)));
        Assert.Single(await Devices(client, a));
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task RevokeOneSession_PreservesOtherSessionsOnSameDevice_AndIsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient();
        await Register(client, factory); var a = await Login(client); var id = Guid.NewGuid();
        var b = await Login(client, id); var c = await Login(client, id);
        Assert.Equal(b.DeviceId, c.DeviceId);
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.NoContent,
            (await Send(client, a, HttpMethod.Delete, $"/api/v1/me/sessions/{b.SessionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, b, HttpMethod.Get, "/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, c, HttpMethod.Get, "/api/v1/auth/me")).StatusCode);
        Assert.Single((await Devices(client, a)).Single(x => x.Id == c.DeviceId).Sessions);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, c, HttpMethod.Delete, $"/api/v1/me/sessions/{c.SessionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, c, HttpMethod.Get, "/api/v1/me/devices")).StatusCode);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task RevokeDevice_CoversEverySessionAndRefresh_PreservesOtherDeviceAndRows()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient();
        await Register(client, factory); var a = await Login(client); var installation = Guid.NewGuid();
        var b = await Login(client, installation); var c = await Login(client, installation);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, a, HttpMethod.Delete,
            $"/api/v1/me/devices/{b.DeviceId}/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, b, HttpMethod.Get, "/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new RefreshRequest(c.RefreshToken, Guid.NewGuid()))).StatusCode);
        Assert.Single(await Devices(client, a));
        Assert.Equal(2, await db.LoginDevices.CountAsync()); Assert.Equal(3, await db.LoginSessions.CountAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, a, HttpMethod.Delete,
            $"/api/v1/me/devices/{b.DeviceId}/sessions")).StatusCode);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task ForeignAndUnknownIds_Return404_AnonymousRequestsReturn401()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient();
        await Register(client, factory); await Register(client, factory, "other@example.test");
        var a = await Login(client); var b = await Login(client, email: "other@example.test");
        foreach (var path in new[] { $"/api/v1/me/devices/{b.DeviceId}/sessions", $"/api/v1/me/sessions/{b.SessionId}",
            $"/api/v1/me/devices/{Guid.NewGuid()}/sessions", $"/api/v1/me/sessions/{Guid.Empty}" })
            Assert.Equal(HttpStatusCode.NotFound, (await Send(client, a, HttpMethod.Delete, path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, b, HttpMethod.Get, "/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"/api/v1/me/sessions/{a.SessionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/auth/logout-all", null)).StatusCode);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task LogoutAll_RevokesCurrentAndOtherDevices_WithoutAffectingAnotherUser()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient();
        await Register(client, factory); await Register(client, factory, "other@example.test");
        var a = await Login(client); var b = await Login(client); var other = await Login(client, email: "other@example.test");
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, a, HttpMethod.Post, "/api/v1/auth/logout-all")).StatusCode);
        foreach (var token in new[] { a, b })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, token, HttpMethod.Get, "/api/v1/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh",
                new RefreshRequest(token.RefreshToken, Guid.NewGuid()))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await Send(client, other, HttpMethod.Get, "/api/v1/auth/me")).StatusCode);
        Assert.Equal(2, await db.Users.CountAsync());
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task ConcurrentRefreshAndRemoteRevocation_NeverResurrectsTheSession_RecoveryWindowIsBounded()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient();
        await Register(client, factory); var a = await Login(client); var b = await Login(client);
        var refresh = client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(b.RefreshToken, Guid.NewGuid()));
        var revoke = Send(client, a, HttpMethod.Delete, $"/api/v1/me/devices/{b.DeviceId}/sessions");
        await Task.WhenAll(refresh, revoke);
        Assert.Equal(HttpStatusCode.NoContent, (await revoke).StatusCode);
        if ((await refresh).IsSuccessStatusCode)
        {
            var rotated = (await (await refresh).Content.ReadFromJsonAsync<TokenResponse>())!;
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, rotated, HttpMethod.Get, "/api/v1/auth/me")).StatusCode);
        }
        else Assert.Equal(HttpStatusCode.Unauthorized, (await refresh).StatusCode);
        var request = new RefreshRequest(a.RefreshToken, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/refresh", request)).StatusCode);
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", request)).StatusCode);
        Assert.All(await db.LoginSessions.ToListAsync(), x => Assert.NotNull(x.RevokedAtUtc));
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task SessionLimit_RejectsExtraLoginWithoutRevokingExistingSessions_ThenAllowsFreedSlot()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString); using var client = factory.CreateClient();
        await Register(client, factory); var install = Guid.NewGuid(); var tokens = new List<TokenResponse>();
        for (var i = 0; i < 10; i++) tokens.Add(await Login(client, install));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(Email,
            Password, new(install, "PC", "Windows", "1")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, tokens[0], HttpMethod.Get, "/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(client, tokens[0], HttpMethod.Delete,
            $"/api/v1/me/sessions/{tokens[1].SessionId}")).StatusCode);
        await Login(client, install);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Migration_BackfillsLastSeen_AndPreservesExistingSession()
    {
        await using var database = await TestDatabase.CreateAsync(); await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync("20261102185629_AccountAuthentication");
        var user = new AppUser { Email = Email, NormalizedEmail = Email.ToUpperInvariant(), UserName = Email };
        var device = new LoginDevice { UserId = user.Id, InstallationId = Guid.NewGuid(), CreatedAtUtc = DateTimeOffset.UtcNow,
            LastSeenAtUtc = DateTimeOffset.UtcNow };
        db.Add(user); db.Add(device); await db.SaveChangesAsync();
        var id = Guid.NewGuid(); var created = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity.sessions ("Id","UserId","DeviceId","SecurityStamp","AccessHash","AccessExpiresAtUtc","CreatedAtUtc","ExpiresAtUtc")
            VALUES ({id},{user.Id},{device.Id},{"stamp"},{"old-hash"},{created.AddMinutes(15)},{created},{created.AddDays(30)})
            """);
        await db.Database.MigrateAsync();
        var session = await db.LoginSessions.SingleAsync();
        Assert.Equal(created, session.LastSeenAtUtc); Assert.Equal(id, session.Id);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
