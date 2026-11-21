using System.Net;
using System.Net.Http.Json;
using FocusTime.Api.Modules.Admin;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Identity.Contracts;
using FocusTime.Api.Modules.Membership;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FocusTime.Api.Tests;

public sealed class AdminOverviewTests
{
    private const string Endpoint = "/api/v1/admin/overview";
    private const string TestPassword = "OverviewTest123";

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Overview_UsesExistingSessionAndAdminPolicy_AndReturnsConsistentStatistics()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var adminId = Guid.NewGuid();
        await using var root = new ApiFactory(database.ConnectionString);
        var now = new DateTimeOffset(2026, 11, 4, 16, 30, 0, TimeSpan.Zero);
        root.Clock.Advance(now - root.Clock.GetUtcNow());
        await using var authorized = root.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["AdminOverview:UserIds:0"] = adminId.ToString() })));
        using var client = authorized.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Endpoint)).StatusCode);

        var empty = (await new AdminOverviewController(db, root.Clock).Get()).Value!;
        Assert.Equal(0, empty.Summary.TotalUsers);
        Assert.Equal(0d, empty.Summary.MemberShare);
        Assert.Empty(empty.Membership);
        Assert.Empty(empty.RecentOrders);
        Assert.Empty(empty.RecentUsers);
        Assert.All(empty.Trend, day => { Assert.Equal(0, day.Users); Assert.Equal(0, day.Members); });

        var admin = User("admin@example.test", now.AddDays(-31), adminId);
        var other = User("other@example.test", now.AddMinutes(-15));
        var yesterday = User("yesterday@example.test", now.AddMinutes(-45));
        var today = User("today@example.test", now.AddMinutes(-1));
        var older = User("older@example.test", now.AddDays(-15));
        var oldest = User("oldest@example.test", now.AddDays(-80));
        db.Users.AddRange(admin, other, yesterday, today, older, oldest);
        await db.SaveChangesAsync();
        db.MembershipGrants.AddRange(
            Grant(admin, "annual", now.AddDays(-12), now.AddDays(30)),
            Grant(admin, "lifetime", now.AddDays(-1), null),
            Grant(yesterday, "monthly", now.AddDays(-10), now),
            Grant(other, "monthly", now.AddMinutes(-15), now.AddDays(30)),
            Grant(today, "annual", now.AddMinutes(-5), now.AddDays(365), now.AddMinutes(-2)),
            Grant(today, "lifetime", now.AddDays(1), null));
        for (var index = 0; index < 6; index++)
        {
            var created = now.AddHours(-index - 1);
            db.MembershipOrders.Add(new()
            {
                Id = Guid.NewGuid(), UserId = other.Id, RequestId = Guid.NewGuid(), Plan = "monthly", PlanName = "月会员", DurationMonths = 1,
                Method = "wechat", AmountFen = 2800, CreatedAtUtc = created,
                ExpiresAtUtc = index == 2 ? now.AddMinutes(1) : created.AddMinutes(20),
                Status = index == 0 ? "paid" : "pending", PaidAtUtc = index == 0 ? created.AddMinutes(5) : null
            });
        }
        await db.SaveChangesAsync();

        var ordinaryToken = await Login(client, other.Email!);
        client.DefaultRequestHeaders.Authorization = new("Bearer", ordinaryToken.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Endpoint)).StatusCode);
        var adminToken = await Login(client, admin.Email!);
        using var defaultClient = root.CreateClient();
        defaultClient.DefaultRequestHeaders.Authorization = new("Bearer", adminToken.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await defaultClient.GetAsync(Endpoint)).StatusCode);

        client.DefaultRequestHeaders.Authorization = new("Bearer", adminToken.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Endpoint + "?days=8")).StatusCode);
        using var response = await client.GetAsync(Endpoint + "?days=7");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var overview = (await response.Content.ReadFromJsonAsync<OverviewResponse>())!;
        Assert.Equal(new OverviewSummary(6, 2, 2, 3, 2d / 6, 1), overview.Summary);
        Assert.Equal(new OverviewDay(new DateOnly(2026, 11, 5), 2, 2), overview.Trend[^1]);
        Assert.Equal(new OverviewDay(new DateOnly(2026, 11, 4), 1, 0), overview.Trend[^2]);
        Assert.Equal(new[] { new OverviewMembership("lifetime", 1, .5), new OverviewMembership("monthly", 1, .5) },
            overview.Membership.OrderBy(plan => plan.Kind));
        Assert.Equal(new[] { today.Id, other.Id, yesterday.Id, older.Id, admin.Id }, overview.RecentUsers.Select(user => user.Id));
        Assert.Equal(new[] { "paid", "expired", "pending", "expired", "expired" }, overview.RecentOrders.Select(order => order.Status));
        Assert.Equal(now.AddHours(-1), overview.RecentOrders[0].CreatedAtUtc);
        foreach (var days in new[] { 7, 30, 90 })
        {
            var data = (await client.GetFromJsonAsync<OverviewResponse>(Endpoint + "?days=" + days))!;
            Assert.Equal(days, data.Trend.Length);
            Assert.Equal(new DateOnly(2026, 11, 5).AddDays(1 - days), data.Trend[0].Date);
            Assert.Equal(overview.Summary, data.Summary);
            Assert.Equal(days == 7 ? 2 : 4, data.Trend.Sum(point => point.Members));
        }
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task AdministratorCookie_RequiresAllowedAccount_PersistsAndIsRevocable()
    {
        const string path = "/api/v1/admin/session";
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await using var root = new ApiFactory(database.ConnectionString);
        var admin = User("admin@example.test", root.Clock.GetUtcNow());
        var ordinary = User("ordinary@example.test", root.Clock.GetUtcNow());
        db.Users.AddRange(admin, ordinary);
        await db.SaveChangesAsync();
        await using var authorized = root.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["AdminOverview:UserIds:0"] = admin.Id.ToString() })));
        using var client = authorized.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"), HandleCookies = false
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Endpoint)).StatusCode);

        LoginRequest Credentials(AppUser user, string password) =>
            new(user.Email!, password, new(Guid.NewGuid(), "Admin browser", "Web", "1"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, Credentials(admin, TestPassword))).StatusCode);
        client.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, Credentials(admin, "Wrong"))).StatusCode);
        using var ordinaryLogin = await client.PostAsJsonAsync(path, Credentials(ordinary, TestPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, ordinaryLogin.StatusCode);
        Assert.False(ordinaryLogin.Headers.Contains("Set-Cookie"));
        Assert.False(await db.LoginSessions.AnyAsync());

        async Task<string> SignIn()
        {
            using var response = await client.PostAsJsonAsync(path, Credentials(admin, TestPassword));
            response.EnsureSuccessStatusCode();
            using var payload = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(admin.Email, payload.RootElement.GetProperty("email").GetString());
            Assert.False(payload.RootElement.TryGetProperty("accessToken", out _));
            Assert.False(payload.RootElement.TryGetProperty("refreshToken", out _));
            var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/api/v1/admin", cookie, StringComparison.OrdinalIgnoreCase);
            return cookie.Split(';', 2)[0];
        }

        client.DefaultRequestHeaders.Add("Cookie", await SignIn());
        var restored = await client.GetFromJsonAsync<AdminSessionProfile>(path);
        Assert.Equal(admin.Email, restored!.Email);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Endpoint)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-FocusTime-Admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
        using var logout = await client.DeleteAsync(path);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains("expires=", Assert.Single(logout.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Endpoint)).StatusCode);

        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", await SignIn());
        root.Clock.Advance(TimeSpan.FromHours(8).Add(TimeSpan.FromSeconds(1)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }


    private static AppUser User(string email, DateTimeOffset created, Guid? id = null)
    {
        var user = new AppUser
        {
            Id = id ?? Guid.NewGuid(), Email = email, NormalizedEmail = email.ToUpperInvariant(),
            UserName = email, NormalizedUserName = email.ToUpperInvariant(),
            EmailConfirmed = true, SecurityStamp = Guid.NewGuid().ToString(), CreatedAtUtc = created
        };
        user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<AppUser>().HashPassword(user, TestPassword);
        return user;
    }

    private static MembershipGrant Grant(AppUser user, string kind, DateTimeOffset start, DateTimeOffset? end,
        DateTimeOffset? revoked = null) => new()
        {
            Id = Guid.NewGuid(), UserId = user.Id, Kind = kind, StartsAtUtc = start,
            CreatedAtUtc = start, ExpiresAtUtc = end, RevokedAtUtc = revoked
        };

    private static async Task<TokenResponse> Login(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(email, TestPassword, new(Guid.NewGuid(), "Test", "Web", "1")));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }
}
