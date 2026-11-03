using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FocusTime.Api.Modules.Identity.Contracts;
using FocusTime.Api.Modules.Sessions.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Tests;

public sealed class AccountTests
{
    private const string Email = "person@example.test";
    private const string Password = "Example12345";
    private static LoginRequest Login(string password = Password, Guid? installation = null) =>
        new(Email, password, new DeviceRegistration(installation ?? Guid.NewGuid(), "Test PC", "Windows", "1.0"));

    private static async Task Register(HttpClient client, ApiFactory factory)
    {
        var challenge = await (await client.PostAsJsonAsync("/api/v1/auth/registration-code", new { Email }))
            .Content.ReadFromJsonAsync<EmailChallengeResponse>();
        var response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(Email, challenge!.ChallengeId, factory.Mail.Code(Email), Password));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
    private static async Task<TokenResponse> SignIn(HttpClient client, LoginRequest? request = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", request ?? Login());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }
    private static async Task<HttpStatusCode> Me(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await client.SendAsync(request)).StatusCode;
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Registration_Login_Rotation_Logout_StoreOnlyDigests()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        await Register(client, factory);
        var login = await SignIn(client);
        Assert.Equal(HttpStatusCode.OK, await Me(client, login.AccessToken));
        var refresh = new RefreshRequest(login.RefreshToken, Guid.NewGuid());
        var rotated = (await (await client.PostAsJsonAsync("/api/v1/auth/refresh", refresh))
            .Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.NotEqual(login.AccessToken, rotated.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(client, login.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await Me(client, rotated.AccessToken));
        var retry = (await (await client.PostAsJsonAsync("/api/v1/auth/refresh", refresh))
            .Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Equal(rotated, retry);
        var user = await db.Users.SingleAsync();
        Assert.NotEqual(Password, user.PasswordHash);
        Assert.True(user.EmailConfirmed);
        Assert.DoesNotContain(await db.RefreshCredentials.ToListAsync(), x => x.TokenHash == login.RefreshToken);
        Assert.DoesNotContain(await db.LoginSessions.ToListAsync(), x => x.AccessHash == login.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/logout",
            new LogoutRequest(rotated.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(client, rotated.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/logout",
            new LogoutRequest(rotated.RefreshToken))).StatusCode);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Refresh_ConcurrentSameRequestIsIdempotent_DifferentReplayRevokesOnlyThatSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        await Register(client, factory);
        var first = await SignIn(client);
        var second = await SignIn(client);
        var request = new RefreshRequest(first.RefreshToken, Guid.NewGuid());
        var replies = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.PostAsJsonAsync("/api/v1/auth/refresh", request)));
        Assert.All(replies, item => Assert.Equal(HttpStatusCode.OK, item.StatusCode));
        var tokens = await Task.WhenAll(replies.Select(x => x.Content.ReadFromJsonAsync<TokenResponse>()));
        Assert.All(tokens, item => Assert.Equal(tokens[0], item));
        var replay = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(client, tokens[0]!.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await Me(client, second.AccessToken));
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task EmailChallenges_RateLimit_AttemptLimit_Expiry_Purpose_AndAtomicConsumption()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/registration-code", new { Email });
        var challenge = (await response.Content.ReadFromJsonAsync<EmailChallengeResponse>())!;
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/v1/auth/registration-code", new { Email })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/password-reset/verify",
            new VerifyPasswordResetRequest(challenge.ChallengeId, factory.Mail.Code(Email)))).StatusCode);
        var wrong = factory.Mail.Code(Email) == "000000" ? "000001" : "000000";
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/register",
                new RegisterRequest(Email, challenge.ChallengeId, wrong, Password))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(Email, challenge.ChallengeId, factory.Mail.Code(Email), Password))).StatusCode);
        Assert.Equal(5, (await db.EmailChallenges.SingleAsync()).Attempts);
        factory.Clock.Advance(TimeSpan.FromMinutes(11));
        var next = (await (await client.PostAsJsonAsync("/api/v1/auth/registration-code", new { Email }))
            .Content.ReadFromJsonAsync<EmailChallengeResponse>())!;
        var register = new RegisterRequest(Email, next.ChallengeId, factory.Mail.Code(Email), Password);
        var results = await Task.WhenAll(client.PostAsJsonAsync("/api/v1/auth/register", register),
            client.PostAsJsonAsync("/api/v1/auth/register", register));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.BadRequest);
        Assert.Single(await db.Users.ToListAsync());
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task PasswordReset_OneUseTicket_RevokesAllSessions_AndChangesPassword()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        await Register(client, factory);
        var first = await SignIn(client);
        var second = await SignIn(client);
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var challenge = (await (await client.PostAsJsonAsync("/api/v1/auth/password-reset/code", new { Email }))
            .Content.ReadFromJsonAsync<EmailChallengeResponse>())!;
        var ticket = (await (await client.PostAsJsonAsync("/api/v1/auth/password-reset/verify",
            new VerifyPasswordResetRequest(challenge.ChallengeId, factory.Mail.Code(Email, "reset"))))
            .Content.ReadFromJsonAsync<PasswordResetTicket>())!;
        var complete = new CompletePasswordResetRequest(ticket.ResetTicket, "CHANGED12345");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/password-reset/complete",
            new CompletePasswordResetRequest(ticket.ResetTicket, "onlyletters"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/password-reset/complete", complete)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/password-reset/complete", complete)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(client, first.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(client, second.AccessToken));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new RefreshRequest(first.RefreshToken, Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", Login())).StatusCode);
        await SignIn(client, Login("CHANGED12345"));
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Expiration_DuplicateRegistration_StableDevice_AndLockout()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        await Register(client, factory);
        var installation = Guid.NewGuid();
        var first = await SignIn(client, Login(installation: installation));
        var second = await SignIn(client, Login(installation: installation));
        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.NotEqual(first.SessionId, second.SessionId);
        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.Unauthorized, await Me(client, first.AccessToken));
        var refreshed = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        factory.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh",
            new RefreshRequest(second.RefreshToken, Guid.NewGuid()))).StatusCode);
        var challenge = (await (await client.PostAsJsonAsync("/api/v1/auth/registration-code", new { Email }))
            .Content.ReadFromJsonAsync<EmailChallengeResponse>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(Email, challenge.ChallengeId, factory.Mail.Code(Email), Password))).StatusCode);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/login", Login("wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/v1/auth/login", Login())).StatusCode);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task MailFailureRollsBack_UnknownResetDoesNotSend_ExpiredCodeRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        factory.Mail.Fail = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/v1/auth/registration-code", new { Email })).StatusCode);
        Assert.Empty(await db.EmailChallenges.ToListAsync());
        factory.Mail.Fail = false;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/password-reset/code", new { Email })).StatusCode);
        Assert.Empty(factory.Mail.Codes);
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var challenge = (await (await client.PostAsJsonAsync("/api/v1/auth/registration-code", new { Email }))
            .Content.ReadFromJsonAsync<EmailChallengeResponse>())!;
        factory.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(Email, challenge.ChallengeId, factory.Mail.Code(Email), Password))).StatusCode);
        db.Users.Add(new() { Email = Email, NormalizedEmail = Email.ToUpperInvariant(),
            UserName = Email, NormalizedUserName = Email.ToUpperInvariant(), EmailConfirmed = true });
        await db.SaveChangesAsync();
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        factory.Mail.Fail = true;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/auth/password-reset/code", new { Email })).StatusCode);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task ProductionWithoutAuthenticationSecret_FailsClosed_WhileHealthRemainsAvailable()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString, environment: "Production") { Secret = "" };
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/v1/auth/registration-code", new { Email })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/v1/auth/login", Login())).StatusCode);
        Assert.Empty(factory.Mail.Codes);
        Assert.Empty(await db.EmailChallenges.ToListAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
