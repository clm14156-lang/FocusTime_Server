using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FocusTime.Api.Modules.Admin;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Identity.Contracts;
using FocusTime.Api.Modules.Membership;
using FocusTime.Api.Modules.Membership.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FocusTime.Api.Tests;

public sealed class MembershipPlanTests
{
    private const string Public = "/api/v1/membership-plans";
    private const string Admin = "/api/v1/admin/membership-plans";
    private const string Orders = "/api/v1/me/membership-orders";
    private const string Password = "CatalogTest12345";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private sealed record Catalog(MembershipPlanResponse[] Plans, bool SimulationAvailable);

    private sealed class Scenario : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required ApiFactory Root { get; init; }
        public required WebApplicationFactory<Program> Factory { get; init; }
        public required HttpClient Client { get; init; }
        public required HttpClient Administrator { get; init; }
        public required Guid UserId { get; init; }
        public static async Task<Scenario> Create(bool legacy = false)
        {
            var database = await TestDatabase.CreateAsync();
            await using var db = database.CreateContext();
            if (legacy) await db.GetService<IMigrator>().MigrateAsync("20261114080000_WebsiteRuleDelta");
            else await db.Database.MigrateAsync();
            var root = new ApiFactory(database.ConnectionString);
            if (legacy) root.Clock.Advance(new DateTimeOffset(2026, 1, 31, 8, 0, 0, TimeSpan.Zero) - root.Clock.GetUtcNow());
            var adminId = Guid.NewGuid(); var userId = Guid.NewGuid();
            await using (var scope = root.Services.CreateAsyncScope())
            {
                var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                foreach (var pair in new[] { (adminId, "catalog-admin@example.test"), (userId, "catalog-user@example.test") })
                    Assert.True((await manager.CreateAsync(new() { Id = pair.Item1, Email = pair.Item2, UserName = pair.Item2, EmailConfirmed = true }, Password)).Succeeded);
            }
            var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["AdminOverview:UserIds:0"] = adminId.ToString() })));
            var client = factory.CreateClient();
            var login = (await (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("catalog-user@example.test", Password,
                new(Guid.NewGuid(), "Catalog test", "Windows", "1")))).Content.ReadFromJsonAsync<TokenResponse>())!;
            client.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);
            var administrator = factory.CreateClient();
            administrator.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
            (await administrator.PostAsJsonAsync("/api/v1/admin/session", new LoginRequest("catalog-admin@example.test", Password,
                new(Guid.NewGuid(), "Catalog administrator", "Web", "1")))).EnsureSuccessStatusCode();
            if (legacy)
            {
                // CookieContainer uses the host clock, not the historical TimeProvider used by this case.
                var adminToken = (await (await administrator.PostAsJsonAsync("/api/v1/auth/login",
                    new LoginRequest("catalog-admin@example.test", Password, new(Guid.NewGuid(), "Legacy migration test", "Web", "1"))))
                    .Content.ReadFromJsonAsync<TokenResponse>())!;
                administrator.DefaultRequestHeaders.Authorization = new("Bearer", adminToken.AccessToken);
            }
            return new() { Database = database, Root = root, Factory = factory, Client = client, Administrator = administrator, UserId = userId };
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); Administrator.Dispose(); await Factory.DisposeAsync(); await Root.DisposeAsync(); await Database.DisposeAsync(); }
    }
    private static async Task<MembershipPlanResponse> Update(Scenario s, string id, int price, int? days, bool available = true)
    {
        var current = (await s.Administrator.GetFromJsonAsync<MembershipPlanResponse[]>(Admin))!.Single(x => x.Id == id);
        var response = await s.Administrator.PutAsJsonAsync(Admin + "/" + id,
            new UpdateMembershipPlanRequest(price, days, available, current.Recommended, current.Version));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MembershipPlanResponse>())!;
    }
    private static async Task<MembershipOrderResponse> Create(Scenario s, string plan, long? version = null, Guid? requestId = null)
    {
        var response = await s.Client.PostAsJsonAsync(Orders, new { plan, method = "wechat", requestId = requestId ?? Guid.NewGuid(), expectedPlanVersion = version, amountFen = 1 });
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<MembershipOrderResponse>())!;
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Catalog_IsPersistedAndAdminOnly_AndEveryNewOrderUsesSavedPrice()
    {
        await using var s = await Scenario.Create();
        using var anonymous = s.Factory.CreateClient();
        var response = await anonymous.GetAsync(Public);
        response.EnsureSuccessStatusCode(); Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(3, (await response.Content.ReadFromJsonAsync<Catalog>())!.Plans.Length);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Client.GetAsync(Admin)).StatusCode);
        foreach (var (id, price, days) in new (string, int, int?)[] { ("monthly", 28000, 30), ("annual", 18888, 400), ("lifetime", 39800, null) })
        {
            var updated = id == "monthly"
                ? (await (await s.Administrator.PutAsJsonAsync(Admin + "/monthly", new { amountFen = price, expectedVersion = 1 }))
                    .Content.ReadFromJsonAsync<MembershipPlanResponse>())!
                : await Update(s, id, price, days);
            Assert.Equal(days, updated.DurationDays);
            Assert.Equal(2, updated.Version);
            var publicPlan = (await anonymous.GetFromJsonAsync<Catalog>(Public))!.Plans.Single(x => x.Id == id);
            Assert.Equal(updated, publicPlan);
            var order = await Create(s, id, updated.Version);
            Assert.Equal(price, order.AmountFen); Assert.Equal(days, order.DurationDays);
            Assert.Equal(updated.Version, order.PlanVersion); Assert.Equal(updated.Name, order.PlanName);
            await using var db = s.Database.CreateContext();
            Assert.Equal(price, (await db.MembershipPlans.AsNoTracking().SingleAsync(x => x.Id == id)).AmountFen);
        }
        using var cors = new HttpRequestMessage(HttpMethod.Get, Public);
        cors.Headers.Add("Origin", "http://127.0.0.1:5175");
        Assert.Equal("http://127.0.0.1:5175", (await anonymous.SendAsync(cors)).Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task ExistingOrdersKeepPriceAndDuration_WhenPlanChangesOrIsDelisted()
    {
        await using var s = await Scenario.Create();
        var request = Guid.NewGuid();
        var old = await Create(s, "annual", requestId: request);
        var changed = await Update(s, "annual", 2500, 405);
        var fresh = await Create(s, "annual", changed.Version);
        Assert.Equal(9800, old.AmountFen); Assert.Equal(365, old.DurationDays);
        Assert.Equal(2500, fresh.AmountFen); Assert.Equal(405, fresh.DurationDays);
        await Update(s, "annual", 3200, 400, false);
        var denied = await s.Client.PostAsJsonAsync(Orders, new { plan = "annual", method = "wechat", requestId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var replay = await Create(s, "annual", requestId: request);
        Assert.Equal(old.Id, replay.Id); Assert.Equal(old.AmountFen, replay.AmountFen); Assert.Equal(old.DurationDays, replay.DurationDays);
        var paidResponse = await s.Client.PostAsync($"{Orders}/{old.Id}/simulate-payment", null);
        paidResponse.EnsureSuccessStatusCode();
        var paid = (await paidResponse.Content.ReadFromJsonAsync<MembershipPaymentResponse>(Json))!;
        Assert.Equal(9800, paid.Order.AmountFen);
        Assert.Equal(paid.Membership.StartsAtUtc!.Value.AddDays(365).ToUnixTimeSeconds(), paid.Membership.ExpiresAtUtc!.Value.ToUnixTimeSeconds());
        var version = paid.Membership.Version; var expiry = paid.Membership.ExpiresAtUtc;
        await Update(s, "annual", 9999, 1);
        var membership = (await s.Client.GetFromJsonAsync<MembershipResponse>("/api/v1/me/membership", Json))!;
        Assert.Equal(expiry, membership.ExpiresAtUtc); Assert.Equal(version, membership.Version);
        var second = await (await s.Client.PostAsync($"{Orders}/{fresh.Id}/simulate-payment", null)).Content.ReadFromJsonAsync<MembershipPaymentResponse>(Json);
        Assert.Equal(expiry!.Value.AddDays(405).ToUnixTimeSeconds(), second!.Membership.ExpiresAtUtc!.Value.ToUnixTimeSeconds());
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task StaleVersionsInvalidTermsAndMissingMutationHeader_CannotChangeCatalogOrCreateWrongOrders()
    {
        await using var s = await Scenario.Create();
        var initial = (await s.Administrator.GetFromJsonAsync<MembershipPlanResponse[]>(Admin))!.Single(x => x.Id == "annual");
        var writes = await Task.WhenAll(new[] { 12000, 13000 }.Select(price => s.Administrator.PutAsJsonAsync(Admin + "/annual",
            new UpdateMembershipPlanRequest(price, 366, true, true, initial.Version))));
        Assert.Single(writes, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(writes, x => x.StatusCode == HttpStatusCode.Conflict);
        var stale = await s.Client.PostAsJsonAsync(Orders, new { plan = "annual", method = "wechat", requestId = Guid.NewGuid(), expectedPlanVersion = initial.Version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await using var db = s.Database.CreateContext(); Assert.Empty(await db.MembershipOrders.ToListAsync());
        foreach (var request in new[] { new UpdateMembershipPlanRequest(0, 30, true, false, 1),
            new UpdateMembershipPlanRequest(1000, 31, true, false, 1), new UpdateMembershipPlanRequest(1000, 36501, true, false, 1),
            new UpdateMembershipPlanRequest(1000, 30, false, null, 1) })
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Administrator.PutAsJsonAsync(Admin + "/monthly", request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Administrator.PutAsJsonAsync(Admin + "/lifetime",
            new UpdateMembershipPlanRequest(1000, 30, true, false, 1))).StatusCode);
        s.Administrator.DefaultRequestHeaders.Remove("X-FocusTime-Admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Administrator.PutAsJsonAsync(Admin + "/monthly",
            new UpdateMembershipPlanRequest(1000, 30, true, false, 1))).StatusCode);
        Assert.Equal(1500, (await db.MembershipPlans.AsNoTracking().SingleAsync(x => x.Id == "monthly")).AmountFen);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Migration_PreservesLegacyCalendarMonthOrders_AfterCatalogDurationChanges()
    {
        await using var s = await Scenario.Create(legacy: true);
        await using var db = s.Database.CreateContext();
        var id = Guid.NewGuid(); var now = s.Root.Clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO membership.orders ("Id", "UserId", "RequestId", "Plan", "PlanName", "DurationMonths",
                "AmountFen", "Method", "Status", "CreatedAtUtc", "ExpiresAtUtc", "ActivationStatus")
            VALUES ({id}, {s.UserId}, {Guid.NewGuid()}, 'monthly', '月会员', 1,
                1500, 'wechat', 'pending', {now}, {now.AddMinutes(15)}, 'notStarted');
            """);
        await db.Database.ExecuteSqlRawAsync("""UPDATE membership.plans SET "DurationDays" = 90 WHERE "Id" = 'monthly';""");
        var legacyRequest = Guid.NewGuid();
        var preUpgrade = await Create(s, "monthly", requestId: legacyRequest);
        Assert.Equal(90, preUpgrade.DurationDays);
        await db.Database.MigrateAsync();
        var saved = await Update(s, "monthly", 5000, 30);
        Assert.Equal(3, saved.Version);
        Assert.Equal(30, (await Create(s, "monthly", saved.Version)).DurationDays);
        Assert.Equal(90, (await Create(s, "monthly", requestId: legacyRequest)).DurationDays);
        var result = await s.Client.PostAsync($"{Orders}/{id}/simulate-payment", null);
        result.EnsureSuccessStatusCode();
        var paid = (await result.Content.ReadFromJsonAsync<MembershipPaymentResponse>(Json))!;
        Assert.Equal(1500, paid.Order.AmountFen); Assert.Equal(1, paid.Order.DurationMonths); Assert.Null(paid.Order.DurationDays);
        Assert.Equal(now.AddMonths(1).ToUnixTimeSeconds(), paid.Membership.ExpiresAtUtc!.Value.ToUnixTimeSeconds());
        await db.Database.MigrateAsync(); Assert.Equal(3, await db.MembershipPlans.CountAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
