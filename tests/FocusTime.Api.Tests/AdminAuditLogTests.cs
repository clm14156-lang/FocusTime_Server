using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

public sealed class AdminAuditLogTests
{
    private const string Logs = "/api/v1/admin/audit-logs";
    private const string Password = "AuditTests12345";
    private sealed class Scenario : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required ApiFactory Root { get; init; }
        public required WebApplicationFactory<Program> Factory { get; init; }
        public required HttpClient Admin { get; init; }
        public required HttpClient User { get; init; }
        public required Guid AdminId { get; init; }
        public required Guid UserId { get; init; }
        public static async Task<Scenario> Create()
        {
            var database = await TestDatabase.CreateAsync();
            await using (var db = database.CreateContext()) await db.Database.MigrateAsync();
            var root = new ApiFactory(database.ConnectionString);
            var adminId = Guid.NewGuid(); var userId = Guid.NewGuid();
            await using (var scope = root.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
                foreach (var pair in new[] { (adminId, "audit-admin@example.test"), (userId, "audit-user@example.test") })
                    Assert.True((await users.CreateAsync(new() { Id = pair.Item1, Email = pair.Item2, UserName = pair.Item2, EmailConfirmed = true }, Password)).Succeeded);
            }
            var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["AdminOverview:UserIds:0"] = adminId.ToString() })));
            var admin = factory.CreateClient(); admin.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
            (await admin.PostAsJsonAsync("/api/v1/admin/session", new LoginRequest("audit-admin@example.test", Password,
                new(Guid.NewGuid(), "Audit test", "Web", "1")))).EnsureSuccessStatusCode();
            var user = factory.CreateClient();
            var login = (await (await user.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("audit-user@example.test", Password,
                new(Guid.NewGuid(), "Audit user", "Web", "1")))).Content.ReadFromJsonAsync<TokenResponse>())!;
            user.DefaultRequestHeaders.Authorization = new("Bearer", login.AccessToken);
            return new() { Database = database, Root = root, Factory = factory, Admin = admin, User = user, AdminId = adminId, UserId = userId };
        }
        public async ValueTask DisposeAsync() { Admin.Dispose(); User.Dispose(); await Factory.DisposeAsync(); await Root.DisposeAsync(); await Database.DisposeAsync(); }
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task PlanChangesAndFailures_ArePersistedAndQueryable_WithoutSecrets()
    {
        await using var s = await Scenario.Create();
        using var anonymous = s.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Logs)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.User.GetAsync(Logs)).StatusCode);
        Assert.Equal(0, (await s.Admin.GetFromJsonAsync<AdminAuditPage>(Logs))!.Total);
        var plans = (await s.Admin.GetFromJsonAsync<MembershipPlanResponse[]>("/api/v1/admin/membership-plans"))!;
        var plan = plans.Single(x => x.Id == "annual");
        var request = new UpdateMembershipPlanRequest(plan.AmountFen + 100, plan.DurationDays, false, plan.Recommended, plan.Version);
        s.Admin.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.99");
        (await s.Admin.PutAsJsonAsync("/api/v1/admin/membership-plans/annual", request)).EnsureSuccessStatusCode();
        var first = (await s.Admin.GetFromJsonAsync<AdminAuditPage>(Logs))!;
        var row = Assert.Single(first.Items);
        Assert.Equal("plans", row.Category); Assert.Equal("success", row.Result.Status);
        Assert.Equal("audit-admin@example.test", row.Administrator);
        Assert.NotEqual("203.0.113.99", row.Ip);
        Assert.Contains(row.Result.Changes, x => x.Field == "套餐价格" && x.Before != x.After);
        Assert.Contains(row.Result.Changes, x => x.Field == "销售状态" && x.After == "已下架");
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PutAsJsonAsync("/api/v1/admin/membership-plans/annual", request)).StatusCode);
        var failures = (await s.Admin.GetFromJsonAsync<AdminAuditPage>(Logs + "?status=failure&category=plans"))!;
        var failure = Assert.Single(failures.Items); Assert.Contains("刷新", failure.Result.Reason);
        var validation = await s.Admin.PutAsJsonAsync("/api/v1/admin/membership-plans/monthly", new UpdateMembershipPlanRequest(0, 30, true, false, 2));
        Assert.Equal(HttpStatusCode.BadRequest, validation.StatusCode);
        Assert.Equal(2, (await s.Admin.GetFromJsonAsync<AdminAuditPage>(Logs + "?status=failure"))!.Total);
        var detailResponse = await s.Admin.GetAsync(Logs + "/" + Uri.EscapeDataString(row.Id));
        detailResponse.EnsureSuccessStatusCode(); Assert.True(detailResponse.Headers.CacheControl?.NoStore);
        var detail = (await detailResponse.Content.ReadFromJsonAsync<AdminAuditLog>())!;
        Assert.Equal(row.Id, detail.Id); Assert.Equal(row.Result.Changes.Count, detail.Result.Changes.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.GetAsync(Logs + "/operation:" + Guid.NewGuid())).StatusCode);
        var raw = await (await s.Admin.GetAsync(Logs)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, raw); Assert.DoesNotContain("accessToken", raw); Assert.DoesNotContain("securityStamp", raw);
        foreach (var suffix in new[] { "page=0", "pageSize=101", "category=bad", "status=bad", "start=2026-11-08&end=2026-11-01" })
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.GetAsync(Logs + "?" + suffix)).StatusCode);
        Assert.Equal(3, (await s.Admin.GetFromJsonAsync<AdminAuditPage>(Logs + "?query=AUDIT-ADMIN"))!.Total);
        Assert.Equal(0, (await s.Admin.GetFromJsonAsync<AdminAuditPage>(Logs + "?query=%25_"))!.Total);
        var page = (await s.Admin.GetFromJsonAsync<AdminAuditPage>(Logs + "?page=2&pageSize=1"))!;
        Assert.Equal(2, page.Page); Assert.Single(page.Items); Assert.Equal(3, page.Total);
        await using var persisted = s.Database.CreateContext(); Assert.Equal(3, await persisted.AdminOperationLogs.CountAsync());
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task MembershipReceiptsAndOrderEvents_ExcludePreviewsAutomationAndDuplicateChildren()
    {
        await using var s = await Scenario.Create();
        var path = $"/api/v1/admin/users/{s.UserId}/membership-adjustments";
        var operation = Guid.NewGuid();
        (await s.Admin.PostAsJsonAsync(path + "/preview", new PreviewAdminMembershipRequest(operation, "grant", "monthly", null, "internal-test", "Audit integration test"))).EnsureSuccessStatusCode();
        Assert.Equal(0, (await s.Admin.GetFromJsonAsync<AdminAuditPage>(Logs))!.Total);
        (await s.Admin.PostAsJsonAsync(path + "/apply", new ApplyAdminMembershipRequest(operation))).EnsureSuccessStatusCode();
        (await s.Admin.PostAsJsonAsync(path + "/apply", new ApplyAdminMembershipRequest(operation))).EnsureSuccessStatusCode();
        var log = Assert.Single((await s.Admin.GetFromJsonAsync<AdminAuditPage>(Logs + "?category=users"))!.Items);
        Assert.Equal("adjustment:" + operation, log.Id); Assert.Equal("audit-user@example.test", log.Target);
        Assert.Contains(log.Result.Changes, x => x.Field == "当前会员" && x.Before == "非会员" && x.After == "月会员");
        var detail = (await s.Admin.GetFromJsonAsync<AdminAuditLog>(Logs + "/" + Uri.EscapeDataString(log.Id)))!;
        Assert.Equal(operation, detail.MembershipGrantId);
        Assert.Contains("Audit integration test", detail.Note);
        (await s.Admin.PostAsJsonAsync(path + "/apply", new ApplyAdminMembershipRequest(Guid.NewGuid()))).StatusCode.Equals(HttpStatusCode.NotFound);
        Assert.Single((await s.Admin.GetFromJsonAsync<AdminAuditPage>(Logs + "?category=users&status=failure"))!.Items);
        await using var db = s.Database.CreateContext();
        var now = s.Root.Clock.GetUtcNow(); var order = new MembershipOrder { Id = Guid.NewGuid(), UserId = s.UserId,
            RequestId = Guid.NewGuid(), Plan = "monthly", PlanName = "月会员", AmountFen = 1500, Method = "wechat",
            DurationDays = 30, CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(15) };
        db.MembershipOrders.Add(order);
        db.MembershipOrderEvents.AddRange(new() { OrderId = order.Id, Kind = "order_created", Actor = "website", OccurredAtUtc = now },
            new() { OrderId = order.Id, Kind = "payment_confirmed", Actor = "simulation", OccurredAtUtc = now },
            new() { OrderId = order.Id, Kind = "membership_activation_failed", Actor = "admin:" + s.AdminId, OccurredAtUtc = now });
        db.AdminSecurityAudits.Add(new() { ActorUserId = s.AdminId, OccurredAtUtc = now, Result = "failure", FailureCode = "current_password_incorrect" });
        await db.SaveChangesAsync();
        var orders = (await s.Admin.GetFromJsonAsync<AdminAuditPage>(Logs + "?category=orders"))!;
        Assert.Equal(1, orders.Total); Assert.Equal("failure", orders.Items[0].Result.Status);
        Assert.Single((await s.Admin.GetFromJsonAsync<AdminAuditPage>(Logs + "?category=settings"))!.Items);
        Assert.Equal(4, (await s.Admin.GetFromJsonAsync<AdminAuditPage>(Logs))!.Total);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task BeijingDateBoundariesAndRollback_AreCorrect()
    {
        await using var s = await Scenario.Create();
        await using var db = s.Database.CreateContext();
        var time = new DateTimeOffset(2026, 11, 6, 16, 0, 0, TimeSpan.Zero);
        foreach (var value in new[] { time.AddTicks(-10), time, time.AddDays(1).AddTicks(-10), time.AddDays(1) })
            db.AdminOperationLogs.Add(new() { ActorUserId = s.AdminId, Administrator = "audit-admin@example.test", OccurredAtUtc = value,
                Category = "plans", Action = "边界测试", Target = "月会员", TargetId = "monthly", Source = "后台 · 套餐管理", Status = "success" });
        await db.SaveChangesAsync();
        Assert.Equal(2, (await s.Admin.GetFromJsonAsync<AdminAuditPage>(Logs + "?start=2026-11-07&end=2026-11-07"))!.Total);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION admin.reject_test_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Status" = 'success' THEN RAISE EXCEPTION 'Injected audit failure'; END IF; RETURN NEW; END; $$;
            CREATE TRIGGER reject_test_audit BEFORE INSERT ON admin.operation_logs FOR EACH ROW EXECUTE FUNCTION admin.reject_test_audit();
            """);
        var plan = (await s.Admin.GetFromJsonAsync<MembershipPlanResponse[]>("/api/v1/admin/membership-plans"))!.Single(x => x.Id == "annual");
        var response = await s.Admin.PutAsJsonAsync("/api/v1/admin/membership-plans/annual", new UpdateMembershipPlanRequest(plan.AmountFen + 50, plan.DurationDays, plan.Available, plan.Recommended, plan.Version));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var after = (await s.Admin.GetFromJsonAsync<MembershipPlanResponse[]>("/api/v1/admin/membership-plans"))!.Single(x => x.Id == "annual");
        Assert.Equal(plan, after);
        Assert.Single((await s.Admin.GetFromJsonAsync<AdminAuditPage>(Logs + "?status=failure"))!.Items);
    }
}
