using System.Net;
using System.Net.Http.Json;
using FocusTime.Api.Modules.Admin;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Identity.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FocusTime.Api.Tests;

public sealed class AdminPasswordTests
{
    private const string Endpoint = "/api/v1/admin/password";
    private const string Password = "OriginalTest123!";
    private const string NextPassword = "ChangedTest456!";

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task PasswordChange_RequiresAdminAndMutationHeader_ValidatesWithoutChangingPassword()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var admin = User("admin@example.test");
        var ordinary = User("ordinary@example.test");
        db.Users.AddRange(admin, ordinary);
        await db.SaveChangesAsync();
        await using var root = new ApiFactory(database.ConnectionString);
        await using var factory = Authorized(root, admin.Id);
        using var client = Client(factory);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Change(client)).StatusCode);
        var ordinaryToken = await Token(client, ordinary);
        client.DefaultRequestHeaders.Authorization = new("Bearer", ordinaryToken.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await Change(client)).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        var cookie = await Cookie(client, admin);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        client.DefaultRequestHeaders.Remove("X-FocusTime-Admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await Change(client)).StatusCode);
        client.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(client, "", NextPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(client, Password, "short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(client, Password, NextPassword, "different")).StatusCode);
        await Code(await Change(client, Password, Password), "password_unchanged");
        await Code(await Change(client, Password, "nodigitshere"), "password_policy_failed");
        await Code(await Change(client, "WrongCurrent123!", NextPassword), "current_password_incorrect");
        db.ChangeTracker.Clear();
        var persisted = await db.Users.SingleAsync(x => x.Id == admin.Id);
        Assert.NotEqual(PasswordVerificationResult.Failed, new PasswordHasher<AppUser>().VerifyHashedPassword(persisted, persisted.PasswordHash!, Password));
        Assert.Equal(1, persisted.AccessFailedCount);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/admin/session")).StatusCode);
        var audits = await db.AdminSecurityAudits.ToListAsync();
        Assert.Equal(3, audits.Count);
        Assert.All(audits, audit => { Assert.Equal("failure", audit.Result); Assert.Equal(admin.Id, audit.ActorUserId); });
        var serialized = System.Text.Json.JsonSerializer.Serialize(audits);
        Assert.DoesNotContain(Password, serialized);
        Assert.DoesNotContain(NextPassword, serialized);
        Assert.DoesNotContain(persisted.PasswordHash!, serialized);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task PasswordChange_HashesAndAuditsAtomically_RevokesOnlyThisAccountsSessionsAndRecoveryTickets()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var admin = User("admin@example.test");
        var ordinary = User("ordinary@example.test");
        db.Users.AddRange(admin, ordinary);
        await db.SaveChangesAsync();
        await using var root = new ApiFactory(database.ConnectionString);
        await using var factory = Authorized(root, admin.Id);
        using var client = Client(factory);
        var firstCookie = await Cookie(client, admin);
        var secondCookie = await Cookie(client, admin);
        var adminToken = await Token(client, admin);
        var ordinaryToken = await Token(client, ordinary);
        db.EmailChallenges.Add(new() { Email = admin.NormalizedEmail!, Purpose = "reset", CreatedAtUtc = root.Clock.GetUtcNow(),
            ExpiresAtUtc = root.Clock.GetUtcNow().AddMinutes(10), TicketHash = "test-ticket", TicketExpiresAtUtc = root.Clock.GetUtcNow().AddMinutes(10) });
        await db.SaveChangesAsync();
        client.DefaultRequestHeaders.Add("Cookie", firstCookie);
        using var changed = await Change(client);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Contains("expires=", Assert.Single(changed.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/session")).StatusCode);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", secondCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/session")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", adminToken.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(adminToken.RefreshToken, Guid.NewGuid()))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", ordinaryToken.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        db.ChangeTracker.Clear();
        var persisted = await db.Users.SingleAsync(x => x.Id == admin.Id);
        var hasher = new PasswordHasher<AppUser>();
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(persisted, persisted.PasswordHash!, Password));
        Assert.NotEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(persisted, persisted.PasswordHash!, NextPassword));
        Assert.NotEqual(admin.SecurityStamp, persisted.SecurityStamp);
        Assert.All(await db.LoginSessions.Where(x => x.UserId == admin.Id).ToListAsync(), session => Assert.NotNull(session.RevokedAtUtc));
        Assert.Null((await db.LoginSessions.SingleAsync(x => x.UserId == ordinary.Id)).RevokedAtUtc);
        var reset = await db.EmailChallenges.SingleAsync();
        Assert.True(reset.Consumed && reset.TicketConsumed);
        var audit = await db.AdminSecurityAudits.SingleAsync();
        Assert.Equal(admin.Id, audit.ActorUserId);
        Assert.Equal("admin.password.change", audit.Operation);
        Assert.Equal("admin-web", audit.Source);
        Assert.Equal("success", audit.Result);
        Assert.Null(audit.FailureCode);
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Remove("Cookie");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/admin/session", Credentials(admin, Password))).StatusCode);
        Assert.NotEmpty(await Cookie(client, admin, NextPassword));
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task ConcurrentPasswordChanges_OnlyOneSucceedsWithTheOriginalSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var admin = User("admin@example.test");
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        await using var root = new ApiFactory(database.ConnectionString);
        await using var factory = Authorized(root, admin.Id);
        using var client = Client(factory);
        client.DefaultRequestHeaders.Add("Cookie", await Cookie(client, admin));
        var results = await Task.WhenAll(Change(client), Change(client));
        Assert.Equal(new[] { HttpStatusCode.NoContent, HttpStatusCode.Unauthorized }, results.Select(x => x.StatusCode).OrderBy(x => x));
        Assert.Single(await db.AdminSecurityAudits.ToListAsync());
        foreach (var response in results) response.Dispose();
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task AuditPersistenceFailure_RollsBackPasswordAndRevocation()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var admin = User("admin@example.test");
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        await using var root = new ApiFactory(database.ConnectionString);
        await using var factory = Authorized(root, admin.Id);
        using var client = Client(factory);
        client.DefaultRequestHeaders.Add("Cookie", await Cookie(client, admin));
        // Only this generated, disposable test database is changed.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE identity.admin_security_audit");
        Assert.Equal(HttpStatusCode.InternalServerError, (await Change(client)).StatusCode);
        db.ChangeTracker.Clear();
        var persisted = await db.Users.SingleAsync(x => x.Id == admin.Id);
        Assert.Equal(admin.PasswordHash, persisted.PasswordHash);
        Assert.Equal(admin.SecurityStamp, persisted.SecurityStamp);
        Assert.Null((await db.LoginSessions.SingleAsync()).RevokedAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/admin/session")).StatusCode);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Authorized(ApiFactory root, Guid id) =>
        root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["AdminOverview:UserIds:0"] = id.ToString() })));
    private static HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
        return client;
    }
    private static LoginRequest Credentials(AppUser user, string password = Password) => new(user.Email!, password, new(Guid.NewGuid(), "Test", "Web", "1"));
    private static Task<HttpResponseMessage> Change(HttpClient client, string current = Password, string next = NextPassword, string? confirm = null) =>
        client.PostAsJsonAsync(Endpoint, new AdminPasswordRequest { CurrentPassword = current, NewPassword = next, ConfirmPassword = confirm ?? next });
    private static async Task Code(HttpResponseMessage response, string code)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var payload = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(code, payload.RootElement.GetProperty("code").GetString());
        }
    }
    private static async Task<string> Cookie(HttpClient client, AppUser user, string password = Password)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/admin/session", Credentials(user, password));
        response.EnsureSuccessStatusCode();
        return Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0];
    }
    private static async Task<TokenResponse> Token(HttpClient client, AppUser user)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", Credentials(user));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }
    private static AppUser User(string email)
    {
        var user = new AppUser { Id = Guid.NewGuid(), Email = email, NormalizedEmail = email.ToUpperInvariant(),
            UserName = email, NormalizedUserName = email.ToUpperInvariant(), EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(), LockoutEnabled = true };
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, Password);
        return user;
    }
}
