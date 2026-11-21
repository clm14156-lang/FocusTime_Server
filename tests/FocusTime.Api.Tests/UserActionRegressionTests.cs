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

namespace FocusTime.Api.Tests;

public sealed class UserActionRegressionTests
{
    private const string Password = "UserAction12345";
    private const string Me = "/api/v1/me/membership";
    private const string Orders = "/api/v1/me/membership-orders";

    private sealed class Scenario : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required ApiFactory Root { get; init; }
        public required WebApplicationFactory<Program> Factory { get; init; }
        public required AppUser Administrator { get; init; }
        public required AppUser Target { get; init; }
        public required HttpClient Admin { get; init; }
        public string Path => $"/api/v1/admin/users/{Target.Id}";

        public static async Task<Scenario> Create()
        {
            var database = await TestDatabase.CreateAsync();
            var root = new ApiFactory(database.ConnectionString);
            var admin = NewUser("actions-admin@example.test", root.Clock.GetUtcNow());
            var target = NewUser("actions-user@example.test", root.Clock.GetUtcNow());
            await using (var db = database.CreateContext())
            {
                await db.Database.MigrateAsync();
                db.Users.AddRange(admin, target);
                await db.SaveChangesAsync();
            }
            var factory = root.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AdminOverview:UserIds:0"] = admin.Id.ToString() })));
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
            (await client.PostAsJsonAsync("/api/v1/admin/session", Login(admin.Email!, Password))).EnsureSuccessStatusCode();
            return new() { Database = database, Root = root, Factory = factory, Administrator = admin, Target = target, Admin = client };
        }

        public Task<HttpResponseMessage> Change(string action, Guid operationId) =>
            Admin.PostAsJsonAsync($"{Path}/{action}", new AdminUserActionRequest(operationId));

        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            await Factory.DisposeAsync();
            await Root.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task DisableRestoreAndSignOutPersistAuditsAndCannotBeBypassedByPasswordReset()
    {
        await using var s = await Scenario.Create();
        using var device1 = s.Factory.CreateClient();
        using var device2 = s.Factory.CreateClient();
        var first = await SignIn(device1, s.Target.Email!);
        await SignIn(device2, s.Target.Email!);
        Assert.Equal(HttpStatusCode.Forbidden, (await device1.PostAsJsonAsync($"{s.Path}/disable", new { operationId = Guid.NewGuid() })).StatusCode);
        using (var missingHeader = new HttpRequestMessage(HttpMethod.Post, $"{s.Path}/disable"))
        {
            missingHeader.Content = JsonContent.Create(new { operationId = Guid.NewGuid() });
            s.Admin.DefaultRequestHeaders.Remove("X-FocusTime-Admin");
            Assert.Equal(HttpStatusCode.Forbidden, (await s.Admin.SendAsync(missingHeader)).StatusCode);
            s.Admin.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
        }
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsJsonAsync($"/api/v1/admin/users/{s.Administrator.Id}/disable", new { operationId = Guid.NewGuid() })).StatusCode);

        var disable = Guid.NewGuid();
        (await s.Change("disable", disable)).EnsureSuccessStatusCode();
        Assert.Equal("disabled", (await s.Admin.GetFromJsonAsync<AdminUser>(s.Path))!.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device1.GetAsync(Me)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device2.GetAsync(Me)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device1.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken!, Guid.NewGuid()))).StatusCode);

        using var fresh = s.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await fresh.PostAsJsonAsync("/api/v1/auth/login", Login(s.Target.Email!, Password))).StatusCode);
        var email = s.Target.Email!;
        var challenge = (await (await fresh.PostAsJsonAsync("/api/v1/auth/password-reset/code", new { email })).Content.ReadFromJsonAsync<EmailChallengeResponse>())!;
        var ticket = (await (await fresh.PostAsJsonAsync("/api/v1/auth/password-reset/verify",
            new VerifyPasswordResetRequest(challenge.ChallengeId, s.Root.Mail.Code(email, "reset")))).Content.ReadFromJsonAsync<PasswordResetTicket>())!;
        const string updatedPassword = "UserActionNew12345";
        (await fresh.PostAsJsonAsync("/api/v1/auth/password-reset/complete", new CompletePasswordResetRequest(ticket.ResetTicket, updatedPassword))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await fresh.PostAsJsonAsync("/api/v1/auth/login", Login(email, updatedPassword))).StatusCode);

        var restore = Guid.NewGuid();
        (await s.Change("restore", restore)).EnsureSuccessStatusCode();
        (await s.Change("disable", disable)).EnsureSuccessStatusCode();
        Assert.Equal("active", (await s.Admin.GetFromJsonAsync<AdminUser>(s.Path))!.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device1.GetAsync(Me)).StatusCode);
        await SignIn(device1, email, updatedPassword);
        await SignIn(device2, email, updatedPassword);

        var signOut = Guid.NewGuid();
        (await s.Change("sessions/revoke-all", signOut)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await device1.GetAsync(Me)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device2.GetAsync(Me)).StatusCode);
        await SignIn(fresh, email, updatedPassword);
        (await s.Change("sessions/revoke-all", signOut)).EnsureSuccessStatusCode();
        (await fresh.GetAsync(Me)).EnsureSuccessStatusCode();

        var logs = (await s.Admin.GetFromJsonAsync<AdminUserAuditPage>(s.Path + "/audit?pageSize=20"))!;
        Assert.Equal(new[] { "disable", "restore", "sign-out" }, logs.Items.Where(row => row.Result == "success").Select(row => row.Type).Order().ToArray());
        foreach (var id in new[] { disable, restore, signOut })
            Assert.Equal("success", (await s.Admin.GetFromJsonAsync<AdminAuditLog>($"/api/v1/admin/audit-logs/operation:{id}"))!.Result.Status);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task RevocationScopesAndConfirmationPreservePaymentAndAuditTheRevocation()
    {
        await using var s = await Scenario.Create();
        using var purchaser = s.Factory.CreateClient();
        await SignIn(purchaser, s.Target.Email!);
        var orderResponse = await purchaser.PostAsJsonAsync(Orders, new { plan = "annual", method = "wechat", requestId = Guid.NewGuid() });
        orderResponse.EnsureSuccessStatusCode();
        var order = (await orderResponse.Content.ReadFromJsonAsync<MembershipOrderResponse>())!;
        (await purchaser.PostAsync($"{Orders}/{order.Id}/simulate-payment", null)).EnsureSuccessStatusCode();
        s.Root.Clock.Advance(TimeSpan.FromSeconds(1));
        var path = s.Path + "/membership-adjustments";
        var paidContext = (await s.Admin.GetFromJsonAsync<AdminMembershipAdjustmentContext>(path))!;
        Assert.True(paidContext.CanRevokeAll);
        Assert.Equal(0, paidContext.GiftGrantCount);
        var gift = Guid.NewGuid();
        (await s.Admin.PostAsJsonAsync(path + "/preview", new PreviewAdminMembershipRequest(gift, "grant", "monthly", null, "promotion", null))).EnsureSuccessStatusCode();
        (await s.Admin.PostAsJsonAsync(path + "/apply", new ApplyAdminMembershipRequest(gift))).EnsureSuccessStatusCode();

        var unknown = Guid.NewGuid();
        await using (var db = s.Database.CreateContext())
        {
            var now = s.Root.Clock.GetUtcNow();
            db.MembershipGrants.Add(new() { Id = unknown, UserId = s.Target.Id, Kind = "monthly", StartsAtUtc = now, ExpiresAtUtc = now.AddDays(7), CreatedAtUtc = now });
            await db.SaveChangesAsync();
        }

        var revoke = Guid.NewGuid();
        var plan = await s.Admin.PostAsJsonAsync(path + "/preview", new PreviewAdminMembershipRequest(revoke, "revoke", null, "admin-gifts", "issued-in-error", null));
        plan.EnsureSuccessStatusCode();
        Assert.Equal(1, (await plan.Content.ReadFromJsonAsync<AdminMembershipAdjustmentView>())!.AffectedGrantCount);
        (await s.Admin.PostAsJsonAsync(path + "/apply", new ApplyAdminMembershipRequest(revoke))).EnsureSuccessStatusCode();
        await using var verify = s.Database.CreateContext();
        Assert.NotNull((await verify.MembershipGrants.SingleAsync(g => g.Id == gift)).RevokedAtUtc);
        Assert.Null((await verify.MembershipGrants.SingleAsync(g => g.Id == order.Id)).RevokedAtUtc);
        Assert.Null((await verify.MembershipGrants.SingleAsync(g => g.Id == unknown)).RevokedAtUtc);
        Assert.Equal("paid", (await verify.MembershipOrders.SingleAsync(o => o.Id == order.Id)).Status);
        Assert.Equal(0, (await s.Admin.GetFromJsonAsync<AdminMembershipAdjustmentContext>(path))!.GiftGrantCount);

        var all = Guid.NewGuid();
        const string note = "核实后停用会员，款项另行处理";
        var request = new PreviewAdminMembershipRequest(all, "revoke", null, "all", "other", note);
        var preview = await s.Admin.PostAsJsonAsync(path + "/preview", request);
        preview.EnsureSuccessStatusCode();
        var view = (await preview.Content.ReadFromJsonAsync<AdminMembershipAdjustmentView>())!;
        Assert.Equal(2, view.AffectedGrantCount);
        Assert.True(view.Before.IsActive);
        Assert.False(view.After.IsActive);
        Assert.False(view.Applied);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.PostAsJsonAsync(path + "/apply", new ApplyAdminMembershipRequest(all))).StatusCode);
        Assert.Null((await verify.MembershipGrants.AsNoTracking().SingleAsync(g => g.Id == order.Id)).RevokedAtUtc);
        Assert.Empty(await verify.MembershipOrderEvents.Where(e => e.OrderId == order.Id && e.Kind == "membership_revoked").ToListAsync());

        var confirm = new ApplyAdminMembershipRequest(all, true);
        (await s.Admin.PostAsJsonAsync(path + "/apply", confirm)).EnsureSuccessStatusCode();
        (await s.Admin.PostAsJsonAsync(path + "/apply", confirm)).EnsureSuccessStatusCode();
        Assert.NotNull((await verify.MembershipGrants.AsNoTracking().SingleAsync(g => g.Id == order.Id)).RevokedAtUtc);
        Assert.NotNull((await verify.MembershipGrants.AsNoTracking().SingleAsync(g => g.Id == unknown)).RevokedAtUtc);
        Assert.Equal("paid", (await verify.MembershipOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
        Assert.False(await verify.MembershipOrderRefunds.AnyAsync(refund => refund.OrderId == order.Id));
        var orderDetail = (await s.Admin.GetFromJsonAsync<AdminOrder>($"/api/v1/admin/orders/{order.Id}"))!;
        Assert.Equal("paid", orderDetail.Order.Status);
        Assert.NotNull(orderDetail.MembershipRevokedAtUtc);
        Assert.StartsWith("admin-adjustment:", Assert.Single(orderDetail.Events, e => e.Kind == "membership_revoked").Actor);
        Assert.DoesNotContain(orderDetail.Events, e => e.Kind.StartsWith("refund_"));
        var log = (await s.Admin.GetFromJsonAsync<AdminAuditLog>($"/api/v1/admin/audit-logs/adjustment:{all}"))!;
        Assert.Equal("撤销全部会员权益", log.Action);
        Assert.Contains(note, log.Note);
        Assert.Equal("success", log.Result.Status);
        var userLog = (await s.Admin.GetFromJsonAsync<AdminUserAuditPage>(s.Path + "/audit?pageSize=20"))!;
        Assert.Contains(userLog.Items, row => row.Id == $"adjustment:{all}" && row.Content == "撤销全部会员权益" && row.Note!.Contains(note));
        var after = (await s.Admin.GetFromJsonAsync<AdminMembershipAdjustmentContext>(path))!;
        Assert.False(after.Current.IsActive);
        Assert.False(after.CanRevokeAll);
        var entitlement = await purchaser.GetFromJsonAsync<System.Text.Json.JsonElement>(Me);
        Assert.False(entitlement.GetProperty("isActive").GetBoolean());

        var refund = new AdminOrderRefundRequest(Guid.NewGuid(), order.AmountFen, "within-guarantee", null, true);
        var refundResponse = await s.Admin.PostAsJsonAsync($"/api/v1/admin/orders/{order.Id}/refund", refund);
        refundResponse.EnsureSuccessStatusCode();
        var refunded = (await refundResponse.Content.ReadFromJsonAsync<AdminOrder>())!;
        Assert.Equal("refunded", refunded.Order.Status);
        Assert.Single(refunded.Events, e => e.Kind == "membership_revoked");
    }

    private static LoginRequest Login(string email, string password) => new(email, password, new(Guid.NewGuid(), "Test", "Web", "1"));

    private static async Task<TokenResponse> SignIn(HttpClient client, string email, string password = Password)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", Login(email, password));
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        return token;
    }

    private static AppUser NewUser(string email, DateTimeOffset now)
    {
        var user = new AppUser { Id = Guid.NewGuid(), Email = email, NormalizedEmail = email.ToUpperInvariant(), UserName = email,
            NormalizedUserName = email.ToUpperInvariant(), EmailConfirmed = true, LockoutEnabled = true,
            CreatedAtUtc = now, SecurityStamp = Guid.NewGuid().ToString() };
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, Password);
        return user;
    }
}
