using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FocusTime.Api.Modules.Admin;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Identity.Contracts;
using FocusTime.Api.Modules.Membership;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FocusTime.Api.Tests;

public sealed class AdminUsersTests
{
    private const string Path = "/api/v1/admin/users";
    private const string Password = "UsersTest123!";

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Users_RegistrationTrialUsesGrantSourceAcrossListAndMembershipDetails()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await using var root = new ApiFactory(database.ConnectionString);
        var now = root.Clock.GetUtcNow();
        var admin = User("trial-list-admin@example.test", now);
        var monthly = User("trial-list-monthly@example.test", now);
        db.Users.AddRange(admin, monthly);
        db.MembershipGrants.Add(Grant(monthly, "monthly", now, now.AddDays(7)));
        await db.SaveChangesAsync();
        await using var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AdminOverview:UserIds:0"] = admin.Id.ToString() })));
        using var registrar = factory.CreateClient();
        const string email = "trial-list-registered@example.test";
        using var emailResponse = await registrar.PostAsJsonAsync("/api/v1/auth/registration-code", new { email });
        emailResponse.EnsureSuccessStatusCode();
        var challenge = (await emailResponse.Content.ReadFromJsonAsync<EmailChallengeResponse>())!;
        using var registration = await registrar.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(
            email, challenge.ChallengeId, root.Mail.Code(email), Password, new(Guid.NewGuid(), "Website", "Web", "1"), Source: "website"));
        registration.EnsureSuccessStatusCode();
        var userId = (await registration.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user").GetProperty("id").GetGuid();
        root.Clock.Advance(TimeSpan.FromSeconds(1));

        using var administrator = factory.CreateClient();
        await SignIn(administrator, admin);
        var list = (await administrator.GetFromJsonAsync<AdminUsersPage>(Path + "?membership=member"))!;
        var trial = Assert.Single(list.Items, x => x.Id == userId);
        var other = Assert.Single(list.Items, x => x.Id == monthly.Id);
        Assert.True(trial.MembershipIsTrial);
        Assert.False(other.MembershipIsTrial);
        Assert.Equal(other.MembershipExpiresAtUtc, trial.MembershipExpiresAtUtc);
        Assert.Equal("monthly", trial.MembershipPlan);
        Assert.Equal("monthly", other.MembershipPlan);
        Assert.Equal(trial, await administrator.GetFromJsonAsync<AdminUser>(Path + "/" + userId));
        var details = (await administrator.GetFromJsonAsync<AdminUserMembership>(Path + "/" + userId + "/membership"))!.Current!;
        Assert.True(details.IsTrial);
        Assert.Equal("registration-trial", details.Source);
        Assert.Equal(trial.MembershipExpiresAtUtc, details.ExpiresAtUtc);
        Assert.False((await administrator.GetFromJsonAsync<AdminUserMembership>(Path + "/" + monthly.Id + "/membership"))!.Current!.IsTrial);
        var adjustment = (await administrator.GetFromJsonAsync<AdminMembershipAdjustmentContext>(
            Path + "/" + userId + "/membership-adjustments"))!;
        Assert.True(adjustment.Current.IsTrial);
        Assert.Equal(userId, adjustment.Current.GrantId);
        var entitled = await new MembershipService(db, root.Clock).GetAsync(userId);
        Assert.True(entitled.IsTrial);
        Assert.True(entitled.IsActive);
        Assert.Equal(2, entitled.Entitlements.Count);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Users_AreAdminOnly_EmptyDataAndMissingDetailsRemainEmpty()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await using var root = new ApiFactory(database.ConnectionString);
        using var client = root.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path + "/" + Guid.NewGuid())).StatusCode);
        var empty = await new AdminUsersController(db, root.Clock).List(new(), default);
        Assert.Empty(empty.Items);
        Assert.Equal(new AdminUserStats(0, 0, 0, 0), empty.Stats);
        Assert.Equal(1, empty.Page);
        var user = User("ordinary@example.test", root.Clock.GetUtcNow());
        db.Users.Add(user); await db.SaveChangesAsync();
        await SignIn(client, user);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path + "/" + user.Id)).StatusCode);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Users_StatsFiltersPaginationAndDetailUsePersistedEntitlementsAndBeijingDates()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext(); await db.Database.MigrateAsync();
        await using var root = new ApiFactory(database.ConnectionString);
        var now = new DateTimeOffset(2026, 11, 4, 16, 30, 0, TimeSpan.Zero);
        root.Clock.Advance(now - root.Clock.GetUtcNow());
        var admin = User("admin@example.test", now.AddDays(-30));
        var today = User("member@example.test", now.AddMinutes(-1));
        var yesterday = User("yesterday@example.test", now.AddMinutes(-45));
        var midnight = User("midnight@example.test", now.AddMinutes(-30));
        var disabled = User("disabled@example.test", now.AddDays(-60)); disabled.LockoutEnd = DateTimeOffset.MaxValue;
        var locked = User("locked@example.test", now.AddDays(-2)); locked.LockoutEnd = now.AddMinutes(5);
        var expiredLock = User("unlocked@example.test", now.AddDays(-10)); expiredLock.LockoutEnd = now;
        var escaped = User("a_b%test@example.test", now.AddDays(-90));
        db.Users.AddRange(admin, today, yesterday, midnight, disabled, locked, expiredLock, escaped);
        db.MembershipGrants.AddRange(Grant(today, "monthly", now.AddDays(-1), now.AddDays(10)),
            Grant(today, "annual", now.AddDays(-1), now.AddDays(100)),
            Grant(midnight, "lifetime", now, null), Grant(midnight, "annual", now, now.AddDays(500)),
            Grant(disabled, "annual", now.AddDays(-2), now.AddDays(50)),
            Grant(yesterday, "monthly", now.AddDays(-30), now),
            Grant(locked, "annual", now.AddDays(1), now.AddDays(366)),
            Grant(expiredLock, "monthly", now.AddDays(-10), now.AddDays(20), now.AddMinutes(-1)));
        var device = new LoginDevice { UserId = today.Id, InstallationId = Guid.NewGuid(), CreatedAtUtc = now, LastSeenAtUtc = now };
        db.LoginDevices.Add(device);
        db.LoginSessions.Add(new() { UserId = today.Id, DeviceId = device.Id, AccessHash = Guid.NewGuid().ToString("N"),
            SecurityStamp = today.SecurityStamp!, CreatedAtUtc = now.AddMinutes(-10), LastSeenAtUtc = now,
            AccessExpiresAtUtc = now.AddMinutes(5), ExpiresAtUtc = now.AddDays(5), RevokedAtUtc = now });
        await db.SaveChangesAsync();
        await using var authorized = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AdminOverview:UserIds:0"] = admin.Id.ToString() })));
        using var client = authorized.CreateClient(); await SignIn(client, admin);
        async Task<AdminUsersPage> Query(string query = "")
        {
            using var response = await client.GetAsync(Path + query); response.EnsureSuccessStatusCode();
            Assert.True(response.Headers.CacheControl?.NoStore);
            var page = (await response.Content.ReadFromJsonAsync<AdminUsersPage>())!;
            Assert.Equal(new AdminUserStats(8, 3, 2, 1), page.Stats);
            return page;
        }
        var all = await Query(); Assert.Equal(8, all.Total);
        Assert.Equal(today.Id, all.Items[0].Id);
        Assert.Equal("annual", all.Items[0].MembershipPlan);
        Assert.Equal(now.AddDays(100), all.Items[0].MembershipExpiresAtUtc);
        Assert.Equal(now.AddMinutes(-10), all.Items[0].LastLoginAtUtc);
        Assert.Equal("lifetime", all.Items.Single(x => x.Id == midnight.Id).MembershipPlan);
        Assert.Equal("locked", all.Items.Single(x => x.Id == locked.Id).Status);
        Assert.Equal("active", all.Items.Single(x => x.Id == expiredLock.Id).Status);
        Assert.Equal("disabled", all.Items.Single(x => x.Id == disabled.Id).Status);
        Assert.Equal(3, (await Query("?quick=members")).Total);
        Assert.Equal(2, (await Query("?quick=today")).Total);
        Assert.Equal(1, (await Query("?quick=inactive")).Total);
        Assert.Equal(0, (await Query("?quick=members&membership=nonMember")).Total);
        Assert.Equal(0, (await Query("?quick=today&account=disabled")).Total);
        Assert.Equal(5, (await Query("?membership=nonMember")).Total);
        Assert.Equal(6, (await Query("?account=active")).Total);
        Assert.Equal(1, (await Query("?account=locked")).Total);
        Assert.Equal(0, (await Query("?account=deleted")).Total);
        Assert.Equal(2, (await Query("?registration=today")).Total);
        Assert.Equal(4, (await Query("?registration=week")).Total);
        Assert.Single((await Query("?query=" + today.Id.ToString()[..8])).Items);
        Assert.Single((await Query("?query=MEMBER%40example.test")).Items);
        Assert.Equal(escaped.Id, Assert.Single((await Query("?query=" + Uri.EscapeDataString("a_b%"))).Items).Id);
        var page2 = await Query("?page=2&pageSize=2"); Assert.Equal(2, page2.Items.Count); Assert.Equal(2, page2.Page);
        Assert.Empty(page2.Items.Select(x => x.Id).Intersect(all.Items.Take(2).Select(x => x.Id)));
        Assert.Equal(4, (await Query("?page=100&pageSize=2")).Page);
        var detail = (await client.GetFromJsonAsync<AdminUser>(Path + "/" + today.Id))!;
        Assert.Equal(all.Items[0], detail);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Path + "/" + Guid.NewGuid())).StatusCode);
        foreach (var invalid in new[] { "?page=0", "?pageSize=101", "?quick=bogus", "?account=bogus", "?registration=bogus", "?membership=bogus" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path + invalid)).StatusCode);
        var json = await client.GetStringAsync(Path);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh", json, StringComparison.OrdinalIgnoreCase);
    }

    private static MembershipGrant Grant(AppUser user, string kind, DateTimeOffset start, DateTimeOffset? end, DateTimeOffset? revoked = null) =>
        new() { Id = Guid.NewGuid(), UserId = user.Id, Kind = kind, StartsAtUtc = start, ExpiresAtUtc = end, CreatedAtUtc = start, RevokedAtUtc = revoked };
    private static AppUser User(string email, DateTimeOffset created)
    {
        var user = new AppUser { Id = Guid.NewGuid(), Email = email, NormalizedEmail = email.ToUpperInvariant(), UserName = email,
            NormalizedUserName = email.ToUpperInvariant(), EmailConfirmed = true, LockoutEnabled = true, CreatedAtUtc = created, SecurityStamp = Guid.NewGuid().ToString() };
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, Password); return user;
    }
    private static async Task SignIn(HttpClient client, AppUser user)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email!, Password, new(Guid.NewGuid(), "Test", "Web", "1")));
        response.EnsureSuccessStatusCode(); var token = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
    }
}
