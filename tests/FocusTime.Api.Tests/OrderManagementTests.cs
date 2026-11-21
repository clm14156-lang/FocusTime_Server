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
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;

namespace FocusTime.Api.Tests;

public sealed class OrderManagementTests
{
    private const string Orders = "/api/v1/me/membership-orders";
    private const string AdminOrders = "/api/v1/admin/orders";
    private const string Password = "OrderTest12345";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    private sealed class Scenario : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required ApiFactory Factory { get; init; }
        public required Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Authorized { get; init; }
        public required HttpClient User { get; init; }
        public required HttpClient Other { get; init; }
        public required HttpClient Admin { get; init; }
        public required TokenResponse Token { get; init; }
        public static async Task<Scenario> Create()
        {
            var database = await TestDatabase.CreateAsync();
            await using (var db = database.CreateContext()) await db.Database.MigrateAsync();
            var factory = new ApiFactory(database.ConnectionString);
            using var setup = factory.CreateClient();
            async Task<UserProfile> Register(string email)
            {
                var code = await (await setup.PostAsJsonAsync("/api/v1/auth/registration-code", new { email })).Content.ReadFromJsonAsync<EmailChallengeResponse>();
                var response = await setup.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, code!.ChallengeId, factory.Mail.Code(email), Password));
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<UserProfile>())!;
            }
            var admin = await Register("orders-admin@example.test");
            await Register("orders-user@example.test");
            await Register("orders-other@example.test");
            var authorized = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["AdminOverview:UserIds:0"] = admin.Id.ToString() })));
            var user = authorized.CreateClient();
            var token = await Login(user, "orders-user@example.test");
            user.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
            var other = authorized.CreateClient();
            other.DefaultRequestHeaders.Authorization = new("Bearer", (await Login(other, "orders-other@example.test")).AccessToken);
            var administrator = authorized.CreateClient();
            administrator.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
            (await administrator.PostAsJsonAsync("/api/v1/admin/session", new LoginRequest(admin.Email, Password,
                new(Guid.NewGuid(), "Order administrator", "Web", "1")))).EnsureSuccessStatusCode();
            return new() { Database = database, Factory = factory, Authorized = authorized, User = user, Other = other, Admin = administrator, Token = token };
        }
        public async ValueTask DisposeAsync()
        {
            User.Dispose(); Other.Dispose(); Admin.Dispose();
            await Authorized.DisposeAsync(); await Factory.DisposeAsync(); await Database.DisposeAsync();
        }
    }

    private static async Task<TokenResponse> Login(HttpClient client, string email) =>
        (await (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password,
            new(Guid.NewGuid(), "Windows client", "Windows", "1")))).Content.ReadFromJsonAsync<TokenResponse>())!;
    private static async Task<MembershipOrderResponse> Create(HttpClient client, string plan = "monthly")
    {
        var response = await client.PostAsJsonAsync(Orders, new { plan, method = "wechat", requestId = Guid.NewGuid(), amountFen = 1 });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MembershipOrderResponse>())!;
    }
    private static async Task<MembershipPaymentResponse> Pay(HttpClient client, Guid id)
    {
        var response = await client.PostAsync($"{Orders}/{id}/simulate-payment", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MembershipPaymentResponse>(Json))!;
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task ChangingPaymentMethodKeepsOrderWorkingWithoutGeneratingProcessingRecords()
    {
        await using var s = await Scenario.Create();
        var order = await Create(s.User);
        foreach (var method in new[] { "wechat", "alipay", "alipay", "wechat" })
        {
            var changed = await s.User.PostAsJsonAsync($"{Orders}/{order.Id}/method", new { method });
            changed.EnsureSuccessStatusCode();
            var result = (await changed.Content.ReadFromJsonAsync<MembershipOrderResponse>())!;
            Assert.Equal(method, result.Method);
            Assert.Equal("pending", result.Status);
        }
        var pending = (await s.Admin.GetFromJsonAsync<AdminOrder>($"{AdminOrders}/{order.Id}"))!;
        Assert.Equal("order_created", Assert.Single(pending.Events).Kind);
        Assert.Equal("paid", (await Pay(s.User, order.Id)).Order.Status);
        await using var db = s.Database.CreateContext();
        Assert.Equal(0, await db.MembershipOrderEvents.CountAsync(x => x.OrderId == order.Id && x.Kind == "payment_method_changed"));
        Assert.Equal(1, await db.MembershipOrderEvents.CountAsync(x => x.OrderId == order.Id && x.Kind == "payment_confirmed"));
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task WebsitePayment_AdminOrders_AndClientEntitlementsUseTheSamePersistedData()
    {
        await using var s = await Scenario.Create();
        var created = await Create(s.User);
        var other = await Create(s.Other, "annual");
        Assert.Equal(1500, created.AmountFen);
        Assert.Equal("notStarted", created.ActivationStatus);
        var before = (await s.Admin.GetFromJsonAsync<AdminOrdersPage>(AdminOrders))!;
        Assert.Equal(2, before.Total);
        Assert.Equal(0, before.Stats.Single(x => x.Key == "paid").Count);
        Assert.False((await s.User.GetFromJsonAsync<MembershipResponse>("/api/v1/me/membership", Json))!.IsActive);
        var confirmations = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Pay(s.User, created.Id)));
        Assert.All(confirmations, result =>
        {
            Assert.Equal("paid", result.Order.Status);
            Assert.Equal("activated", result.Order.ActivationStatus);
            Assert.Equal(created.Id, result.Order.MembershipGrantId);
            Assert.Equal(1, result.Membership.Version);
            Assert.Equal("monthly", result.Membership.Plan);
        });
        await using var db = s.Database.CreateContext();
        Assert.Single(await db.MembershipGrants.ToListAsync());
        Assert.Single(await db.MembershipAudits.ToListAsync());
        Assert.Equal(1, await db.MembershipOrderEvents.CountAsync(x => x.OrderId == created.Id && x.Kind == "payment_confirmed"));
        var page = (await s.Admin.GetFromJsonAsync<AdminOrdersPage>(AdminOrders + "?status=paid&query=" + created.OrderNumber))!;
        var row = Assert.Single(page.Items);
        Assert.Equal(s.Token.User.Id, row.UserId);
        Assert.Equal(s.Token.User.Email, row.Email);
        Assert.NotNull(row.Order.ActivatedAtUtc);
        Assert.NotNull(row.MembershipExpiresAtUtc);
        Assert.Equal(1, page.Stats.Single(x => x.Key == "paid").Count);
        var detail = (await s.Admin.GetFromJsonAsync<AdminOrder>($"{AdminOrders}/{created.Id}"))!;
        Assert.Contains(detail.Events, x => x.Kind == "order_created");
        Assert.Contains(detail.Events, x => x.Kind == "membership_activated");
        // Fresh HTTP request and DbContext see the same data after refresh.
        Assert.Equal("activated", (await s.Admin.GetFromJsonAsync<AdminOrder>($"{AdminOrders}/{created.Id}"))!.Order.ActivationStatus);
        Assert.Equal("paid", (await db.MembershipOrders.AsNoTracking().SingleAsync(x => x.Id == created.Id)).Status);
        Assert.False((await s.Other.GetFromJsonAsync<MembershipResponse>("/api/v1/me/membership", Json))!.IsActive);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Other.PostAsync($"{Orders}/{created.Id}/simulate-payment", null)).StatusCode);
        Assert.DoesNotContain((await s.Other.GetFromJsonAsync<MembershipOrderResponse[]>(Orders))!, x => x.Id == created.Id);
        var windows = await Login(s.User, s.Token.User.Email);
        Assert.Equal("annual", windows.User.Membership); // Existing Windows contract maps a timed plan to Annual.
        s.User.DefaultRequestHeaders.Authorization = new("Bearer", windows.AccessToken);
        var lease = await (await s.User.PostAsJsonAsync("/api/v1/me/entitlement-lease",
            new EntitlementLeaseRequest(new('a', 64), new('b', 64), new('c', 64)))).Content.ReadFromJsonAsync<EntitlementLeaseResponse>();
        Assert.NotNull(lease);
        var payload = lease.Token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(payload + new string('=', (4 - payload.Length % 4) % 4)));
        Assert.Equal(s.Token.User.Id.ToString(), claims.RootElement.GetProperty("sub").GetString());
        Assert.Equal(2, claims.RootElement.GetProperty("rights").GetArrayLength());
        Assert.Equal(confirmations[0].Membership.ExpiresAtUtc!.Value.ToUnixTimeSeconds(), claims.RootElement.GetProperty("grantExp").GetInt64());
        Assert.Equal(9800, other.AmountFen);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task GrantFailure_PreservesPaymentAndException_AndAdminRetryGrantsExactlyOnce()
    {
        await using var s = await Scenario.Create();
        var order = await Create(s.User, "annual");
        await using var db = s.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION membership.reject_test_grant() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected membership storage failure'; END; $$;
            CREATE TRIGGER reject_test_grant BEFORE INSERT ON membership.grants
            FOR EACH ROW EXECUTE FUNCTION membership.reject_test_grant();
            """);
        var failed = await Pay(s.User, order.Id);
        Assert.Equal("paid", failed.Order.Status);
        Assert.Equal("failed", failed.Order.ActivationStatus);
        Assert.Equal("membership_activation_failed", failed.Order.ActivationErrorCode);
        Assert.Null(failed.Order.MembershipGrantId);
        Assert.False(failed.Membership.IsActive);
        Assert.Empty(await db.MembershipGrants.ToListAsync());
        Assert.Empty(await db.MembershipStates.ToListAsync());
        Assert.Empty(await db.MembershipAudits.ToListAsync());
        var exceptions = (await s.Admin.GetFromJsonAsync<AdminOrdersPage>(AdminOrders + "?exceptionOnly=true"))!;
        Assert.Equal(1, exceptions.Total);
        Assert.Equal(1, exceptions.Stats.Single(x => x.Key == "exceptions").Count);
        Assert.Equal("paid", (await db.MembershipOrders.AsNoTracking().SingleAsync()).Status);
        var retry = $"{AdminOrders}/{order.Id}/retry-activation";
        Assert.Equal(HttpStatusCode.Forbidden, (await s.User.PostAsync(retry, null)).StatusCode);
        s.Admin.DefaultRequestHeaders.Remove("X-FocusTime-Admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Admin.PostAsync(retry, null)).StatusCode);
        s.Admin.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
        var stillFailed = await (await s.Admin.PostAsync(retry, null)).Content.ReadFromJsonAsync<AdminOrder>();
        Assert.Equal("failed", stillFailed!.Order.ActivationStatus);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_test_grant ON membership.grants; DROP FUNCTION membership.reject_test_grant();");
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var response = await s.Admin.PostAsync(retry, null);
            response.EnsureSuccessStatusCode();
            Assert.Equal("activated", (await response.Content.ReadFromJsonAsync<AdminOrder>())!.Order.ActivationStatus);
        }
        Assert.Single(await db.MembershipGrants.ToListAsync());
        Assert.Single(await db.MembershipAudits.ToListAsync());
        Assert.Equal(1, (await db.MembershipStates.AsNoTracking().SingleAsync()).Version);
        var after = (await s.Admin.GetFromJsonAsync<AdminOrdersPage>(AdminOrders + "?exceptionOnly=true"))!;
        Assert.Equal(0, after.Total);
        Assert.Equal(0, after.Stats.Single(x => x.Key == "exceptions").Count);
        var detail = (await s.Admin.GetFromJsonAsync<AdminOrder>($"{AdminOrders}/{order.Id}"))!;
        Assert.Contains(detail.Events, x => x.Kind == "membership_activation_failed");
        Assert.Contains(detail.Events, x => x.Kind == "membership_activated" && x.Actor.StartsWith("admin:"));
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task AdminSearchFiltersPaginationAndExport_AreAuthorizedAndUseRealOrders()
    {
        await using var s = await Scenario.Create();
        using var anonymous = s.Authorized.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(AdminOrders)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.User.GetAsync(AdminOrders)).StatusCode);
        var first = await Create(s.User);
        await Create(s.User, "annual"); await Create(s.User, "lifetime"); await Create(s.Other);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsync($"{AdminOrders}/{first.Id}/retry-activation", null)).StatusCode);
        var page = (await s.Admin.GetFromJsonAsync<AdminOrdersPage>(AdminOrders + "?pageSize=2&page=2"))!;
        Assert.Equal(4, page.Total); Assert.Equal(2, page.Page); Assert.Equal(2, page.Items.Count);
        foreach (var suffix in new[] { "plan=annual", "plan=lifetime", "query=" + first.OrderNumber, "query=orders-other" })
            Assert.Single((await s.Admin.GetFromJsonAsync<AdminOrdersPage>(AdminOrders + "?" + suffix))!.Items);
        Assert.Empty((await s.Admin.GetFromJsonAsync<AdminOrdersPage>(AdminOrders + "?query=%25_"))!.Items);
        Assert.Single((await s.Admin.GetFromJsonAsync<AdminOrdersPage>(AdminOrders + "?query=&plan=annual&status=pending"))!.Items);
        Assert.Equal(3, (await s.Admin.GetFromJsonAsync<AdminOrdersPage>(AdminOrders + "?userId=" + s.Token.User.Id))!.Total);
        Assert.Equal(4, (await s.Admin.GetFromJsonAsync<AdminOrder[]>(AdminOrders + "/export?pageSize=1"))!.Length);
        foreach (var suffix in new[] { "page=0", "pageSize=101", "plan=yearly", "method=card", "range=invalid" })
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.GetAsync(AdminOrders + "?" + suffix)).StatusCode);
        s.Factory.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(4, (await s.Admin.GetFromJsonAsync<AdminOrdersPage>(AdminOrders + "?status=closed"))!.Total);
        Assert.Equal(0, (await s.Admin.GetFromJsonAsync<AdminOrdersPage>(AdminOrders + "?status=pending"))!.Total);
        await using var production = new ApiFactory(s.Database.ConnectionString, environment: "Production");
        using var productionClient = production.CreateClient();
        productionClient.DefaultRequestHeaders.Authorization = new("Bearer", s.Token.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await productionClient.PostAsync($"{Orders}/{first.Id}/simulate-payment", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await productionClient.PostAsJsonAsync(Orders,
            new { plan = "monthly", method = "wechat", requestId = Guid.NewGuid() })).StatusCode);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task UserHistory_FiltersPaidAndRefundedBeforeCountingAndPaging()
    {
        await using var s = await Scenario.Create();
        var expected = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var order = await Create(s.User);
            await Pay(s.User, order.Id);
            if (i == 2)
            {
                var refund = new AdminOrderRefundRequest(Guid.NewGuid(), order.AmountFen, "within-guarantee", null, true);
                (await s.Admin.PostAsJsonAsync($"{AdminOrders}/{order.Id}/refund", refund)).EnsureSuccessStatusCode();
            }
            expected.Insert(0, order.Id);
            s.Factory.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        // Newer excluded rows must not consume any of the first five positions.
        await Create(s.User);
        var other = await Create(s.Other);
        await Pay(s.Other, other.Id);
        var query = $"{AdminOrders}?status=paid-or-refunded&userId={s.Token.User.Id}&pageSize=5";
        var first = (await s.Admin.GetFromJsonAsync<AdminOrdersPage>(query))!;
        var second = (await s.Admin.GetFromJsonAsync<AdminOrdersPage>(query + "&page=2"))!;
        Assert.Equal(6, first.Total);
        Assert.Equal(6, second.Total);
        Assert.Equal(2, second.Page);
        Assert.Equal(expected.Take(5), first.Items.Select(row => row.Order.Id));
        Assert.Equal(expected[5], Assert.Single(second.Items).Order.Id);
        Assert.Contains(first.Items, row => row.Order.Status == "refunded");
        Assert.All(first.Items, row => Assert.Equal(s.Token.User.Id, row.UserId));
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Migration_PreservesLegacyPaidOrders_AndBackfillsOnlyProvenMemberships()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync("20261105065917_AdminPasswordSecurityAudit");
        var user = new AppUser { Id = Guid.NewGuid(), Email = "legacy@example.test", UserName = "legacy@example.test",
            NormalizedEmail = "LEGACY@EXAMPLE.TEST", NormalizedUserName = "LEGACY@EXAMPLE.TEST" };
        // Seed the historical schema without the columns added by later migrations.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity.users ("Id","CreatedAtUtc","UserName","NormalizedUserName","Email","NormalizedEmail","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount")
            VALUES ({user.Id},{user.CreatedAtUtc},{user.UserName},{user.NormalizedUserName},{user.Email},{user.NormalizedEmail},false,false,false,false,0)
            """);
        var paid = Guid.NewGuid(); var missing = Guid.NewGuid(); var now = DateTimeOffset.UtcNow.AddDays(-1);
        foreach (var id in new[] { paid, missing })
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO membership.orders ("Id", "UserId", "RequestId", "Plan", "AmountFen", "Method", "Status", "CreatedAtUtc", "ExpiresAtUtc", "PaidAtUtc")
                VALUES ({id}, {user.Id}, {Guid.NewGuid()}, 'lifetime', 19800, 'wechat', 'paid', {now}, {now.AddMinutes(15)}, {now.AddMinutes(1)});
                """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO membership.grants ("Id", "UserId", "Kind", "StartsAtUtc", "ExpiresAtUtc", "CreatedAtUtc", "RevokedAtUtc")
            VALUES ({paid}, {user.Id}, 'lifetime', {now}, NULL, {now.AddMinutes(1)}, NULL);
            """);
        await db.Database.MigrateAsync();
        Assert.Equal("activated", (await db.MembershipOrders.AsNoTracking().SingleAsync(x => x.Id == paid)).ActivationStatus);
        Assert.Equal("failed", (await db.MembershipOrders.AsNoTracking().SingleAsync(x => x.Id == missing)).ActivationStatus);
        Assert.Equal(6, await db.MembershipOrderEvents.CountAsync());
        await db.Database.MigrateAsync();
        Assert.Equal(2, await db.MembershipOrders.CountAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
