using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FocusTime.Api.Modules.Admin;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Identity.Contracts;
using FocusTime.Api.Modules.Membership.Contracts;
using FocusTime.Api.Modules.Sessions.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FocusTime.Api.Tests;

public sealed class RegistrationAutoLoginTests
{
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task AdministratorTrialRule_IsValidatedAndPersisted_AndAppliesOnlyToNewRegistrations()
    {
        const string path = "/api/v1/admin/registration-trial";
        const string password = "Register12345";
        const string adminEmail = "trial-admin@example.test";
        var adminId = Guid.NewGuid();
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await using var root = new ApiFactory(database.ConnectionString);
        await using var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AdminOverview:UserIds:0"] = adminId.ToString() })));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            Assert.True((await manager.CreateAsync(new AppUser
                { Id = adminId, Email = adminEmail, UserName = adminEmail, EmailConfirmed = true }, password)).Succeeded);
        }

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        var (existingUser, existingRequest) = await Register("old-trial@example.test");
        Assert.Equal(7, Duration(existingUser));
        using var ordinary = factory.CreateClient();
        ordinary.DefaultRequestHeaders.Authorization = new("Bearer", existingUser.AccessToken);
        ordinary.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
        Assert.Equal(HttpStatusCode.Forbidden, (await ordinary.PutAsJsonAsync(path, new { durationDays = 12, expectedVersion = 1 })).StatusCode);
        using var admin = await SignInAdmin();
        Assert.Equal(new RegistrationTrialRuleResponse(7, 1), await admin.GetFromJsonAsync<RegistrationTrialRuleResponse>(path));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync(path, new { durationDays = 12, expectedVersion = 1 })).StatusCode);
        admin.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
        foreach (var invalid in new[] { "0", "16", "3.5", "\"3\"", "null" })
        {
            using var body = new StringContent("{\"durationDays\":" + invalid + ",\"expectedVersion\":1}", System.Text.Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync(path, body)).StatusCode);
        }

        var update = await admin.PutAsJsonAsync(path, new { durationDays = 15, expectedVersion = 1 });
        update.EnsureSuccessStatusCode();
        Assert.Equal(new RegistrationTrialRuleResponse(15, 2), await update.Content.ReadFromJsonAsync<RegistrationTrialRuleResponse>());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync(path, new { durationDays = 1, expectedVersion = 1 })).StatusCode);

        using var freshSession = await SignInAdmin();
        Assert.Equal(new RegistrationTrialRuleResponse(15, 2), await freshSession.GetFromJsonAsync<RegistrationTrialRuleResponse>(path));
        Assert.Equal(15, (await db.RegistrationTrialRules.AsNoTracking().SingleAsync()).DurationDays);
        var (newUser, _) = await Register("new-trial@example.test");
        Assert.Equal(15, Duration(newUser));
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/v1/auth/register", existingRequest)).StatusCode);
        var originalGrant = await db.MembershipGrants.AsNoTracking().SingleAsync(x => x.UserId == existingUser.User.Id);
        Assert.Equal(existingUser.User.MembershipStatus!.ExpiresAtUtc, originalGrant.ExpiresAtUtc);

        async Task<HttpClient> SignInAdmin()
        {
            var client = factory.CreateClient();
            using var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/session")
            {
                Content = JsonContent.Create(new LoginRequest(adminEmail, password, new(Guid.NewGuid(), "Test", "Web", "1")))
            };
            login.Headers.Add("X-FocusTime-Admin", "1");
            using var response = await client.SendAsync(login);
            response.EnsureSuccessStatusCode();
            return client;
        }

        async Task<(TokenResponse, RegisterRequest)> Register(string email)
        {
            using var challengeResponse = await anonymous.PostAsJsonAsync("/api/v1/auth/registration-code", new { email });
            challengeResponse.EnsureSuccessStatusCode();
            var challenge = (await challengeResponse.Content.ReadFromJsonAsync<EmailChallengeResponse>())!;
            var request = new RegisterRequest(email, challenge.ChallengeId, root.Mail.Code(email), password,
                new(Guid.NewGuid(), "Website", "Web", "1"), Source: "website");
            using var response = await anonymous.PostAsJsonAsync("/api/v1/auth/register", request);
            response.EnsureSuccessStatusCode();
            return ((await response.Content.ReadFromJsonAsync<TokenResponse>(ApiJson))!, request);
        }

        static double Duration(TokenResponse user)
        {
            var membership = Assert.IsType<MembershipResponse>(user.User.MembershipStatus);
            Assert.True(membership.IsTrial);
            return (membership.ExpiresAtUtc!.Value - membership.StartsAtUtc!.Value).TotalDays;
        }
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Registration_AutoLogin_ReturnsAtomicCredentials_AndRejectsInvalidOrReplayedRequests()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        const string email = "autologin@example.test", password = "Register12345";
        var challenge = (await (await client.PostAsJsonAsync("/api/v1/auth/registration-code", new { email }))
            .Content.ReadFromJsonAsync<EmailChallengeResponse>())!;
        var code = factory.Mail.Code(email);
        var device = new DeviceRegistration(Guid.NewGuid(), "Website", "Web", "1.0");
        var request = new RegisterRequest(email, challenge.ChallengeId, code, password, device, Source: "website");
        var invalidDevice = await client.PostAsJsonAsync("/api/v1/auth/register", request with { Device = device with { InstallationId = Guid.Empty } });
        Assert.Equal(HttpStatusCode.BadRequest, invalidDevice.StatusCode);
        Assert.Empty(await db.Users.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.LoginSessions.AsNoTracking().ToArrayAsync());
        Assert.False((await db.EmailChallenges.AsNoTracking().SingleAsync()).Consumed);
        var invalidCode = await client.PostAsJsonAsync("/api/v1/auth/register", request with { Code = code == "000000" ? "000001" : "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidCode.StatusCode);
        Assert.Empty(await db.Users.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.MembershipGrants.AsNoTracking().ToArrayAsync());
        var registrations = await Task.WhenAll(client.PostAsJsonAsync("/api/v1/auth/register", request),
            client.PostAsJsonAsync("/api/v1/auth/register", request));
        using var response = Assert.Single(registrations, x => x.StatusCode == HttpStatusCode.Created);
        using var rejected = Assert.Single(registrations, x => x.StatusCode == HttpStatusCode.BadRequest);
        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>(ApiJson))!;
        Assert.Equal("Bearer", token.TokenType);
        Assert.Equal(email, token.User.Email);
        Assert.True(token.User.EmailConfirmed);
        Assert.Equal("annual", token.User.Membership);
        var membership = Assert.IsType<MembershipResponse>(token.User.MembershipStatus);
        Assert.True(membership.IsActive);
        Assert.True(membership.IsTrial);
        Assert.Equal(membership.StartsAtUtc!.Value.AddDays(7), membership.ExpiresAtUtc);
        Assert.Contains(EntitlementCodes.ForcedMode, membership.Entitlements);
        Assert.Contains(EntitlementCodes.AutomaticBlocking, membership.Entitlements);
        var grant = await db.MembershipGrants.AsNoTracking().SingleAsync();
        Assert.Equal(token.User.Id, grant.UserId);
        Assert.Equal(membership.StartsAtUtc, grant.StartsAtUtc);
        Assert.Equal(membership.ExpiresAtUtc, grant.ExpiresAtUtc);
        var audit = await db.MembershipAudits.AsNoTracking().SingleAsync();
        Assert.Equal(grant.Id, audit.GrantId);
        Assert.Equal("registration-trial", audit.Actor);
        Assert.Equal("免费体验", audit.Reason);
        Assert.Empty(await db.MembershipOrders.AsNoTracking().ToArrayAsync());
        Assert.False(string.IsNullOrEmpty(token.AccessToken));
        Assert.False(string.IsNullOrEmpty(token.RefreshToken));
        Assert.Equal(factory.Clock.GetUtcNow().AddDays(30).ToUnixTimeMilliseconds(), token.RefreshTokenExpiresAtUtc.ToUnixTimeMilliseconds());
        Assert.True((await db.EmailChallenges.AsNoTracking().SingleAsync()).Consumed);
        Assert.Single(await db.Users.AsNoTracking().ToArrayAsync());
        var stored = Assert.Single(await db.LoginSessions.AsNoTracking().ToArrayAsync());
        Assert.NotEqual(token.AccessToken, stored.AccessHash);
        Assert.Equal(token.User.Id, stored.UserId);
        Assert.Single(await db.RefreshCredentials.AsNoTracking().ToArrayAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(token.User.Id, (await me.Content.ReadFromJsonAsync<UserProfile>())!.Id);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/auth/register", request)).StatusCode);
        Assert.Single(await db.LoginSessions.AsNoTracking().ToArrayAsync());
        var current = await client.GetFromJsonAsync<MembershipResponse>("/api/v1/me/membership", ApiJson);
        Assert.True(current!.IsTrial);
        Assert.Equal(membership.ExpiresAtUtc, current.ExpiresAtUtc);
        var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(token.RefreshToken, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var anotherChallenge = (await (await client.PostAsJsonAsync("/api/v1/auth/registration-code", new { email }))
            .Content.ReadFromJsonAsync<EmailChallengeResponse>())!;
        using var duplicate = await client.PostAsJsonAsync("/api/v1/auth/register", request with
        {
            ChallengeId = anotherChallenge.ChallengeId,
            Code = factory.Mail.Code(email)
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(grant.ExpiresAtUtc, (await db.MembershipGrants.AsNoTracking().SingleAsync()).ExpiresAtUtc);

        factory.Clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password, device));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var newSession = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Equal("normal", newSession.User.Membership);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newSession.AccessToken);
        var expired = await client.GetFromJsonAsync<MembershipResponse>("/api/v1/me/membership", ApiJson);
        Assert.False(expired!.IsActive);
        Assert.False(expired.IsTrial);
        Assert.Empty(expired.Entitlements);
        Assert.Equal(grant.ExpiresAtUtc, expired.LastExpiredAtUtc);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Registration_GrantFailure_RollsBackAccountAndAllowsRetry()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        const string email = "failed-trial@example.test";
        var challenge = (await (await client.PostAsJsonAsync("/api/v1/auth/registration-code", new { email }))
            .Content.ReadFromJsonAsync<EmailChallengeResponse>())!;
        var request = new RegisterRequest(email, challenge.ChallengeId, factory.Mail.Code(email), "Register12345",
            new DeviceRegistration(Guid.NewGuid(), "Website", "Web", "1.0"), Source: "website");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION public.fail_trial_grant() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Test failure while writing a registration grant'; END;
            $$;
            CREATE TRIGGER fail_trial_grant BEFORE INSERT ON membership.grants
                FOR EACH ROW EXECUTE FUNCTION public.fail_trial_grant();
            """);
        using var failed = await client.PostAsJsonAsync("/api/v1/auth/register", request);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Empty(await db.Users.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.LoginSessions.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.MembershipAudits.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.MembershipGrants.AsNoTracking().ToArrayAsync());
        Assert.False((await db.EmailChallenges.AsNoTracking().SingleAsync()).Consumed);

        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_trial_grant ON membership.grants;");
        using var retry = await client.PostAsJsonAsync("/api/v1/auth/register", request);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.True((await retry.Content.ReadFromJsonAsync<TokenResponse>(ApiJson))!.User.MembershipStatus!.IsTrial);
        Assert.Single(await db.MembershipGrants.AsNoTracking().ToArrayAsync());
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Registration_AutoLogin_PreservesLegacyClientResponseWithoutDevice()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        const string email = "legacy-registration@example.test";
        var challenge = (await (await client.PostAsJsonAsync("/api/v1/auth/registration-code", new { email }))
            .Content.ReadFromJsonAsync<EmailChallengeResponse>())!;
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, challenge.ChallengeId, factory.Mail.Code(email), "Register12345"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = (await response.Content.ReadFromJsonAsync<UserProfile>())!;
        Assert.NotEqual(Guid.Empty, user.Id); Assert.Equal(email, user.Email);
        Assert.Equal("normal", user.Membership);
        Assert.Null(user.MembershipStatus);
        Assert.Empty(await db.MembershipGrants.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.LoginSessions.AsNoTracking().ToArrayAsync());
    }
}
