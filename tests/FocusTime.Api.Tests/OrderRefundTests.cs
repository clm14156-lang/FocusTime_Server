using System.Net;
using System.Net.Http.Json;
using FocusTime.Api.Modules.Admin;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Identity.Contracts;
using FocusTime.Api.Modules.Membership;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FocusTime.Api.Tests;

public sealed class OrderRefundTests
{
    private const string Password = "RefundTest12345";
    private const string Orders = "/api/v1/me/membership-orders";
    private const string AdminOrders = "/api/v1/admin/orders";
    private sealed class Scenario : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required ApiFactory Root { get; init; }
        public required WebApplicationFactory<Program> Factory { get; init; }
        public required HttpClient Admin { get; init; }
        public required HttpClient User { get; init; }
        public required Guid UserId { get; init; }
        public static async Task<Scenario> Create()
        {
            var database = await TestDatabase.CreateAsync();
            await using (var db = database.CreateContext()) await db.Database.MigrateAsync();
            var root = new ApiFactory(database.ConnectionString);
            root.Clock.Advance(-TimeSpan.FromTicks(root.Clock.GetUtcNow().Ticks % 10));
            var adminId = Guid.NewGuid(); var userId = Guid.NewGuid();
            await using (var scope = root.Services.CreateAsyncScope())
            {
                var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                foreach (var pair in new[] { (adminId, "refund-admin@example.test"), (userId, "refund-user@example.test") })
                    Assert.True((await manager.CreateAsync(new() { Id = pair.Item1, Email = pair.Item2, UserName = pair.Item2, EmailConfirmed = true }, Password)).Succeeded);
            }
            var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["AdminOverview:UserIds:0"] = adminId.ToString(), ["RateLimiting:PermitLimit"] = "1000" })));
            var user = factory.CreateClient();
            var token = (await (await user.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("refund-user@example.test", Password,
                new(Guid.NewGuid(), "Refund user", "Windows", "1")))).Content.ReadFromJsonAsync<TokenResponse>())!;
            user.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
            var admin = factory.CreateClient(); admin.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
            var result = new Scenario { Database = database, Root = root, Factory = factory, Admin = admin, User = user, UserId = userId };
            await result.LoginAdmin(); return result;
        }
        public async Task LoginAdmin() => (await Admin.PostAsJsonAsync("/api/v1/admin/session", new LoginRequest("refund-admin@example.test", Password,
            new(Guid.NewGuid(), "Refund administrator", "Web", "1")))).EnsureSuccessStatusCode();
        public async Task<MembershipOrderResponse> CreateOrder(bool pay = true)
        {
            var created = await User.PostAsJsonAsync(Orders, new { plan = "monthly", method = "wechat", requestId = Guid.NewGuid() });
            created.EnsureSuccessStatusCode(); var order = (await created.Content.ReadFromJsonAsync<MembershipOrderResponse>())!;
            if (pay) (await User.PostAsync($"{Orders}/{order.Id}/simulate-payment", null)).EnsureSuccessStatusCode();
            return order;
        }
        public Task<HttpResponseMessage> Refund(Guid orderId, Guid operation, int amount = 1500, bool revoke = true) =>
            Admin.PostAsJsonAsync($"{AdminOrders}/{orderId}/refund", new AdminOrderRefundRequest(operation, amount, "within-guarantee", "订单退款测试", revoke));
        public async ValueTask DisposeAsync() { Admin.Dispose(); User.Dispose(); await Factory.DisposeAsync(); await Root.DisposeAsync(); await Database.DisposeAsync(); }
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task RefundRevokesOnlyTheOrderGrantAndPersistsAllRecordsExactlyOnce()
    {
        await using var s = await Scenario.Create(); var first = await s.CreateOrder(); var second = await s.CreateOrder();
        var before = (await s.Admin.GetFromJsonAsync<AdminOrder>($"{AdminOrders}/{first.Id}"))!;
        Assert.True(before.Refund!.Eligible); Assert.True(before.Refund.Supported); Assert.True(before.Refund.CanRevokeMembership);
        Assert.Equal(before.Order.PaidAtUtc!.Value.AddHours(72), before.Refund.DeadlineAtUtc);
        // A full refund revokes the associated membership even when the caller omits the opt-in.
        var operation = Guid.NewGuid(); var response = await s.Refund(first.Id, operation, first.AmountFen, revoke: false); response.EnsureSuccessStatusCode();
        var detail = (await response.Content.ReadFromJsonAsync<AdminOrder>())!;
        Assert.Equal("refunded", detail.Order.Status); Assert.Equal(first.AmountFen, detail.Refund!.Receipt!.AmountFen);
        Assert.True(detail.Refund.Receipt.MembershipRevoked); Assert.NotNull(detail.MembershipRevokedAtUtc);
        Assert.Contains(detail.Events, x => x.Kind == "refund_requested"); Assert.Contains(detail.Events, x => x.Kind == "refund_completed");
        Assert.Contains(detail.Events, x => x.Kind == "membership_revoked");
        (await s.Refund(first.Id, operation, first.AmountFen, revoke: false)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Refund(first.Id, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsync($"{AdminOrders}/{first.Id}/retry-activation", null)).StatusCode);
        await using var db = s.Database.CreateContext();
        Assert.Single(await db.MembershipOrderRefunds.ToListAsync());
        Assert.NotNull((await db.MembershipGrants.SingleAsync(x => x.Id == first.Id)).RevokedAtUtc);
        Assert.Null((await db.MembershipGrants.SingleAsync(x => x.Id == second.Id)).RevokedAtUtc);
        Assert.Equal(3, (await db.MembershipStates.SingleAsync(x => x.UserId == s.UserId)).Version);
        Assert.Equal(1, await db.MembershipOrderEvents.CountAsync(x => x.OrderId == first.Id && x.Kind == "refund_completed"));
        Assert.Equal(1, await db.AdminOperationLogs.CountAsync(x => x.Status == "success" && x.Action == "记录模拟订单退款"));
        var list = (await s.Admin.GetFromJsonAsync<AdminOrdersPage>(AdminOrders))!;
        Assert.Equal(1, list.Stats.Single(x => x.Key == "refunded").Count);
        Assert.Equal(1, list.Stats.Single(x => x.Key == "paid").Count);
        var audit = (await s.Admin.GetFromJsonAsync<AdminAuditPage>("/api/v1/admin/audit-logs?status=success"))!;
        Assert.Single(audit.Items);
        var userAudit = (await s.Admin.GetFromJsonAsync<AdminUserAuditPage>($"/api/v1/admin/users/{s.UserId}/audit"))!;
        Assert.Contains(userAudit.Items, x => x.Admin == "refund-admin@example.test" && x.Type == "membership.revoke");

        // A page of newer abandoned checkouts must not hide completed orders.
        db.MembershipOrders.AddRange(Enumerable.Range(1, 51).Select(i => new MembershipOrder
        {
            Id = Guid.NewGuid(), UserId = s.UserId, RequestId = Guid.NewGuid(),
            Plan = second.Plan, PlanName = second.PlanName!, AmountFen = second.AmountFen,
            DurationDays = second.DurationDays, PlanVersion = second.PlanVersion, Method = "wechat",
            CreatedAtUtc = second.CreatedAtUtc.AddSeconds(i), ExpiresAtUtc = second.ExpiresAtUtc.AddSeconds(i),
        }));
        await db.SaveChangesAsync();
        var history = (await s.User.GetFromJsonAsync<MembershipOrderResponse[]>(Orders + "?historyOnly=true"))!;
        Assert.Equal(2, history.Length);
        var refunded = Assert.Single(history, x => x.Status == "refunded");
        Assert.Equal(first.Id, refunded.Id);
        Assert.Equal(detail.Refund!.Receipt!.CompletedAtUtc, refunded.RefundedAtUtc);
        Assert.Equal(second.Id, Assert.Single(history, x => x.Status == "paid").Id);
        var fullList = (await s.User.GetFromJsonAsync<MembershipOrderResponse[]>(Orders))!;
        Assert.Equal(50, fullList.Length);
        Assert.All(fullList, order => Assert.Equal("pending", order.Status));
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task PartialRefundCanKeepMembershipAndReplayChecksTheOriginalRequest()
    {
        await using var s = await Scenario.Create(); var order = await s.CreateOrder(); var operation = Guid.NewGuid();
        var response = await s.Refund(order.Id, operation, 500, false); response.EnsureSuccessStatusCode();
        var detail = (await response.Content.ReadFromJsonAsync<AdminOrder>())!;
        Assert.Equal(500, detail.Refund!.Receipt!.AmountFen); Assert.False(detail.Refund.Receipt.MembershipRevoked);
        Assert.Null(detail.MembershipRevokedAtUtc); Assert.DoesNotContain(detail.Events, x => x.Kind == "membership_revoked");
        Assert.Equal(HttpStatusCode.Conflict, (await s.Refund(order.Id, operation, 600, false)).StatusCode);
        await using var db = s.Database.CreateContext();
        Assert.Null((await db.MembershipGrants.SingleAsync()).RevokedAtUtc);
        Assert.Equal(1, (await db.MembershipStates.SingleAsync()).Version);
        var membership = (await s.Admin.GetFromJsonAsync<AdminUserMembership>($"/api/v1/admin/users/{s.UserId}/membership"))!;
        Assert.Equal("order", membership.Current!.Source); Assert.Equal(order.Id, membership.Current.OrderId);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Exact72HoursIsAllowedButBeyondTheDeadlineIsRejected()
    {
        await using var s = await Scenario.Create(); var atBoundary = await s.CreateOrder(); var afterBoundary = await s.CreateOrder();
        s.Root.Clock.Advance(TimeSpan.FromHours(72)); await s.LoginAdmin();
        Assert.True((await s.Admin.GetFromJsonAsync<AdminOrder>($"{AdminOrders}/{atBoundary.Id}"))!.Refund!.Eligible);
        var operation = Guid.NewGuid(); (await s.Refund(atBoundary.Id, operation)).EnsureSuccessStatusCode();
        s.Root.Clock.Advance(TimeSpan.FromMilliseconds(1));
        var blocked = await s.Refund(afterBoundary.Id, Guid.NewGuid()); Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("refund_period_expired", await blocked.Content.ReadAsStringAsync());
        (await s.Refund(atBoundary.Id, operation)).EnsureSuccessStatusCode(); // A successful idempotent retry remains readable after the deadline.
        await using var db = s.Database.CreateContext();
        Assert.Equal("paid", (await db.MembershipOrders.SingleAsync(x => x.Id == afterBoundary.Id)).Status);
        Assert.Null((await db.MembershipGrants.SingleAsync(x => x.Id == afterBoundary.Id)).RevokedAtUtc);
        Assert.Single(await db.MembershipOrderRefunds.ToListAsync());
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task DeadlineStartsAtPaymentNotCreation()
    {
        await using var s = await Scenario.Create(); var order = await s.CreateOrder(false);
        s.Root.Clock.Advance(TimeSpan.FromMinutes(14));
        (await s.User.PostAsync($"{Orders}/{order.Id}/simulate-payment", null)).EnsureSuccessStatusCode();
        s.Root.Clock.Advance(TimeSpan.FromHours(72) - TimeSpan.FromMinutes(10)); await s.LoginAdmin();
        Assert.True(s.Root.Clock.GetUtcNow() > order.CreatedAtUtc.AddHours(72));
        (await s.Refund(order.Id, Guid.NewGuid())).EnsureSuccessStatusCode();
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task PermissionsAmountsUnpaidAndUnsupportedPaymentsCannotMutateOrders()
    {
        await using var s = await Scenario.Create(); var order = await s.CreateOrder(); var unpaid = await s.CreateOrder(false);
        var body = new AdminOrderRefundRequest(Guid.NewGuid(), 1500, "within-guarantee", null, true);
        using var anonymous = s.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{AdminOrders}/{order.Id}/refund", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.User.PostAsJsonAsync($"{AdminOrders}/{order.Id}/refund", body)).StatusCode);
        s.Admin.DefaultRequestHeaders.Remove("X-FocusTime-Admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Admin.PostAsJsonAsync($"{AdminOrders}/{order.Id}/refund", body)).StatusCode);
        s.Admin.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Refund(order.Id, Guid.NewGuid(), 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Refund(order.Id, Guid.NewGuid(), 1501)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Refund(order.Id, Guid.Empty)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Refund(unpaid.Id, Guid.NewGuid())).StatusCode);
        await using var db = s.Database.CreateContext();
        var purchase = await db.MembershipOrders.SingleAsync(x => x.Id == order.Id); purchase.PaymentProvider = "wechat"; purchase.PaymentReference = "isolated-test-real-provider";
        await db.SaveChangesAsync();
        var unsupported = await s.Refund(order.Id, Guid.NewGuid()); Assert.Equal(HttpStatusCode.Conflict, unsupported.StatusCode);
        Assert.Contains("refund_provider_not_supported", await unsupported.Content.ReadAsStringAsync());
        Assert.Empty(await db.MembershipOrderRefunds.ToListAsync());
        Assert.Equal("paid", (await db.MembershipOrders.AsNoTracking().SingleAsync(x => x.Id == order.Id)).Status);
        Assert.Null((await db.MembershipGrants.SingleAsync()).RevokedAtUtc);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task ConcurrentRetriesAndDifferentOperationsCannotRefundTwice()
    {
        await using var s = await Scenario.Create(); var first = await s.CreateOrder(); var second = await s.CreateOrder();
        var operation = Guid.NewGuid(); var same = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => s.Refund(first.Id, operation)));
        Assert.All(same, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        var different = await Task.WhenAll(s.Refund(second.Id, Guid.NewGuid()), s.Refund(second.Id, Guid.NewGuid()));
        Assert.Single(different, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(different, x => x.StatusCode == HttpStatusCode.Conflict);
        await using var db = s.Database.CreateContext(); Assert.Equal(2, await db.MembershipOrderRefunds.CountAsync());
        Assert.Equal(2, await db.MembershipOrderEvents.CountAsync(x => x.Kind == "refund_completed"));
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task SharedGrantBindingCannotRevokeAnotherOrdersRights()
    {
        await using var s = await Scenario.Create(); var first = await s.CreateOrder(); var second = await s.CreateOrder();
        await using var db = s.Database.CreateContext();
        var shared = await db.MembershipOrders.SingleAsync(x => x.Id == second.Id);
        shared.MembershipGrantId = first.Id; await db.SaveChangesAsync();
        var response = await s.Refund(first.Id, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("refund_grant_binding_invalid", await response.Content.ReadAsStringAsync());
        Assert.Empty(await db.MembershipOrderRefunds.ToListAsync());
        Assert.All(await db.MembershipGrants.ToListAsync(), grant => Assert.Null(grant.RevokedAtUtc));
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task DatabaseFailureRollsBackRefundOrderGrantVersionAndSuccessAudit()
    {
        await using var s = await Scenario.Create(); var order = await s.CreateOrder();
        await using var db = s.Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION membership.reject_test_refund() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected refund persistence failure'; END; $$;
            CREATE TRIGGER reject_test_refund BEFORE INSERT ON membership.order_refunds FOR EACH ROW EXECUTE FUNCTION membership.reject_test_refund();
            """);
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Refund(order.Id, Guid.NewGuid())).StatusCode);
        Assert.Empty(await db.MembershipOrderRefunds.ToListAsync());
        Assert.Equal("paid", (await db.MembershipOrders.AsNoTracking().SingleAsync()).Status);
        Assert.Null((await db.MembershipGrants.AsNoTracking().SingleAsync()).RevokedAtUtc);
        Assert.Equal(1, (await db.MembershipStates.AsNoTracking().SingleAsync()).Version);
        Assert.DoesNotContain(await db.MembershipOrderEvents.ToListAsync(), x => x.Kind.StartsWith("refund_") || x.Kind == "membership_revoked");
        Assert.Empty(await db.AdminOperationLogs.Where(x => x.Status == "success").ToListAsync());
    }
}
