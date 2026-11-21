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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FocusTime.Api.Tests;

public sealed class AdminNotificationsTests
{
    private const string Endpoint = "/api/v1/admin/notifications";
    private const string Password = "LocalNotices12345";

    [PostgresFact]
    public async Task WatchdogHistory_IsImportedIdempotentlyAndRecovers()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var clock = new TestClock();
        var dir = Directory.CreateTempSubdirectory("focustime-watchdog-test-");
        var file = Path.Combine(dir.FullName, "watchdog.json");
        var watch = new AdminAlertWatchdog(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AdminAlerts:SpoolDirectory"] = dir.FullName }).Build());
        var collector = new AdminNotificationCollector(db, clock, watchdog: watch);
        var id = Guid.NewGuid();
        var occurredAt = clock.GetUtcNow();
        try
        {
            async Task Import(DateTimeOffset? recoveredAt)
            {
                var at = clock.GetUtcNow();
                await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
                {
                    CheckedAt = at, Incidents = new[]
                    {
                        new { Id = id, Service = "api", Description = "External probe failed", OccurredAt = occurredAt,
                            LastOccurredAt = occurredAt, Count = 3, RecoveredAt = recoveredAt }
                    }
                }));
                var ms = at.ToUnixTimeMilliseconds();
                await collector.ObserveAsync(new MonitorSnapshot(ms, ms, [], []), default);
            }
            await Import(null);
            await Import(null);
            Assert.Null((await db.AdminNotifications.AsNoTracking().SingleAsync()).HandledAtUtc);
            clock.Advance(TimeSpan.FromMinutes(1));
            await Import(clock.GetUtcNow());
            var saved = await db.AdminNotifications.AsNoTracking().SingleAsync();
            Assert.Equal(id, saved.Id);
            Assert.Equal("recovered", saved.Resolution);
            Assert.Equal(3, saved.OccurrenceCount);
            Assert.NotNull(saved.HandledAtUtc);
        }
        finally
        {
            File.Delete(file);
            dir.Delete();
        }
    }

    [PostgresFact]
    [Trait("Category", "PostgreSQL")]
    public async Task OperationalSignals_DeduplicateAndKeepFastMailRecovery()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        var clock = new TestClock();
        var boundary = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds() / 300 * 300);
        clock.Advance(boundary.AddSeconds(2) - clock.GetUtcNow());
        var signals = new AdminAlertSignals(clock, new ConfigurationBuilder().Build());
        var collector = new AdminNotificationCollector(db, clock, signals);

        void SampleApi(int failures)
        {
            for (var i = 0; i < 50; i++)
                signals.RecordRequest("POST", "/api/v1/auth/register", i < failures ? 503 : 201, "test-trace");
        }
        async Task Poll()
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            await collector.ObserveAsync(new MonitorSnapshot(now, now, [], []), default);
        }

        SampleApi(3);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Poll();
        Assert.Empty(await db.AdminNotifications.AsNoTracking().ToListAsync());
        SampleApi(4);
        clock.Advance(TimeSpan.FromMinutes(5));
        for (var i = 0; i < 3; i++) signals.RecordRegistrationMail(false);
        clock.Advance(TimeSpan.FromSeconds(1));
        signals.RecordRegistrationMail(true);
        for (var i = 0; i < 20; i++) signals.RecordLoginFailure("ABCDEFGHIJKLMN", $"account-{i % 5}");
        await Poll();
        await Poll();
        var alerts = await db.AdminNotifications.AsNoTracking().OrderBy(x => x.Kind).ToArrayAsync();
        Assert.Equal(new[] { "api-rate", "login", "mail" }, alerts.Select(x => x.Kind).ToArray());
        Assert.Equal(4, alerts[0].OccurrenceCount);
        Assert.Equal(20, alerts[1].OccurrenceCount);
        Assert.Equal("recovered", alerts[2].Resolution);
        Assert.Equal(3, alerts[2].OccurrenceCount);
        Assert.NotNull(alerts[2].HandledAtUtc);

        SampleApi(0);
        clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));
        await Poll();
        Assert.Equal(3, await db.AdminNotifications.CountAsync(x => x.HandledAtUtc != null));
    }

    [PostgresFact]
    public async Task LiveSources_PersistUniqueNotifications_ReadByAdministrator_AndKeepRecoveredHistory()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var db = database.CreateContext()) await db.Database.MigrateAsync();
        var admins = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var emails = new[] { "notices-admin-a@example.test", "notices-admin-b@example.test" };
        await using var root = new ApiFactory(database.ConnectionString);
        await using var factory = root.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                admins.Select((id, index) => new KeyValuePair<string, string?>($"AdminOverview:UserIds:{index}", id.ToString()))));
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        });
        var orderId = Guid.NewGuid();
        var paidAt = root.Clock.GetUtcNow().AddMinutes(-10);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            for (var index = 0; index < admins.Length; index++)
                Assert.True((await manager.CreateAsync(new() { Id = admins[index], Email = emails[index], UserName = emails[index],
                    EmailConfirmed = true }, Password)).Succeeded);
        }
        await using (var db = database.CreateContext())
        {
            db.MembershipOrders.Add(new()
            {
                Id = orderId, RequestId = Guid.NewGuid(), UserId = admins[0], Plan = "monthly", PlanName = "Monthly",
                AmountFen = 1500, Method = "wechat", DurationDays = 30, CreatedAtUtc = paidAt.AddMinutes(-1),
                ExpiresAtUtc = paidAt.AddMinutes(5), PaidAtUtc = paidAt, Status = "paid",
                ActivationStatus = "failed", ActivationAttemptedAtUtc = paidAt,
            });
            db.MembershipOrders.Add(new()
            {
                Id = Guid.NewGuid(), RequestId = Guid.NewGuid(), UserId = admins[0], Plan = "monthly", PlanName = "Monthly",
                AmountFen = 1500, Method = "wechat", DurationDays = 30, CreatedAtUtc = paidAt,
                ExpiresAtUtc = paidAt.AddMinutes(5), Status = "pending",
            });
            await db.SaveChangesAsync();
        }
        var now = root.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        var apiIncident = new MonitorIncident(Guid.NewGuid().ToString("N"), "api", "health", "danger", "API unavailable", null, now - 1000, null);
        var databaseIncident = new MonitorIncident(Guid.NewGuid().ToString("N"), "database", "health", "danger", "DB unavailable", null, now - 3000, now - 2000);
        var snapshot = new MonitorSnapshot(now, now - 4000,
            [new("api", "unavailable", null, null, "", ""), new("database", "healthy", null, null, "", "")],
            [apiIncident, databaseIncident]);
        async Task Observe(MonitorSnapshot value)
        {
            await using var db = database.CreateContext();
            await new AdminNotificationCollector(db, root.Clock).ObserveAsync(value, default);
        }
        await Task.WhenAll(Observe(snapshot), Observe(snapshot));
        using var first = await Login(emails[0]);
        using var second = await Login(emails[1]);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Endpoint)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await first.GetAsync(Endpoint + "?pageSize=0")).StatusCode);
        var all = (await first.GetFromJsonAsync<AdminNotificationsPage>(Endpoint))!;
        Assert.Equal(3, all.Total);
        Assert.Equal(3, all.UnreadCount);
        var firstCombined = (await first.GetFromJsonAsync<AdminNotificationsPage>(Endpoint + "?includeLatest=true"))!;
        Assert.Null(firstCombined.LatestItems);
        Assert.Equal(all.Items, firstCombined.Items);
        var failedOrder = Assert.Single(all.Items, x => x.OrderId == orderId);
        Assert.Null(failedOrder.HandledAtUtc);
        Assert.Null(Assert.Single(all.Items, x => x.Service == "api").HandledAtUtc);
        Assert.NotNull(Assert.Single(all.Items, x => x.Service == "database").HandledAtUtc);
        Assert.All(all.Items, item => Assert.Null(item.ReadAtUtc));
        var detail = await first.GetFromJsonAsync<AdminNotificationItem>(Endpoint + "/" + failedOrder.Id);
        Assert.Equal(failedOrder, detail);
        var secondPage = (await first.GetFromJsonAsync<AdminNotificationsPage>(Endpoint + "?pageSize=2&page=2"))!;
        Assert.Equal(all.Items[2].Id, Assert.Single(secondPage.Items).Id);
        Assert.Equal(2, secondPage.Page);

        var readUrl = Endpoint + "/" + failedOrder.Id + "/read";
        first.DefaultRequestHeaders.Remove("X-FocusTime-Admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await first.PostAsync(readUrl, null)).StatusCode);
        first.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
        using var read = await first.PostAsync(readUrl, null);
        read.EnsureSuccessStatusCode();
        var after = (await read.Content.ReadFromJsonAsync<AdminNotificationsPage>())!;
        Assert.Equal(2, after.UnreadCount);
        var readOrder = Assert.Single(after.Items, x => x.Id == failedOrder.Id);
        Assert.NotNull(readOrder.ReadAtUtc);
        Assert.Null(readOrder.HandledAtUtc);
        (await first.PostAsync(readUrl, null)).EnsureSuccessStatusCode();
        Assert.Equal(3, (await second.GetFromJsonAsync<AdminNotificationsPage>(Endpoint))!.UnreadCount);
        using var fresh = await Login(emails[0]);
        var unread = (await fresh.GetFromJsonAsync<AdminNotificationsPage>(Endpoint + "?unreadOnly=true"))!;
        Assert.Equal(2, unread.Items.Count);
        Assert.DoesNotContain(unread.Items, x => x.OrderId == orderId);
        var combinedUrl = $"?pageSize=1&unreadOnly=true&includeLatest=true&noticeId={failedOrder.Id}";
        var combined = (await fresh.GetFromJsonAsync<AdminNotificationsPage>(Endpoint + combinedUrl))!;
        Assert.Single(combined.Items);
        Assert.Equal(all.Items.Select(x => x.Id), combined.LatestItems!.Select(x => x.Id));
        Assert.Equal(readOrder, combined.SelectedItem);
        using var readAll = await fresh.PostAsync(Endpoint + "/read-all", null);
        readAll.EnsureSuccessStatusCode();
        var history = (await readAll.Content.ReadFromJsonAsync<AdminNotificationsPage>())!;
        Assert.Equal(3, history.Total);
        Assert.Equal(0, history.UnreadCount);
        Assert.Equal(3, history.Items.Count);
        Assert.Equal(readOrder.ReadAtUtc, Assert.Single(history.Items, x => x.Id == failedOrder.Id).ReadAtUtc);
        using var again = await fresh.PostAsync(Endpoint + "/read-all" + combinedUrl, null);
        again.EnsureSuccessStatusCode();
        var combinedAfterRead = (await again.Content.ReadFromJsonAsync<AdminNotificationsPage>())!;
        Assert.Empty(combinedAfterRead.Items);
        Assert.Equal(0, combinedAfterRead.UnreadCount);
        Assert.Equal(3, combinedAfterRead.LatestItems!.Count);
        Assert.Equal(readOrder, combinedAfterRead.SelectedItem);

        root.Clock.Advance(TimeSpan.FromMinutes(1));
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MembershipOrderService>().ActivateAsync(orderId, "test", default);
        var recoveredAt = root.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        // A restarted monitor may no longer contain the incident which was open.
        await Observe(snapshot with { CheckedAt = recoveredAt,
            Checks = [new("api", "healthy", null, null, "", ""), new("database", "healthy", null, null, "", "")],
            Incidents = [] });
        var recovered = (await first.GetFromJsonAsync<AdminNotificationsPage>(Endpoint))!;
        Assert.All(recovered.Items, item => { Assert.NotNull(item.HandledAtUtc); Assert.NotNull(item.ReadAtUtc); });
        Assert.Equal("fulfilled", Assert.Single(recovered.Items, x => x.Id == failedOrder.Id).Resolution);
        Assert.Equal(3, (await second.GetFromJsonAsync<AdminNotificationsPage>(Endpoint))!.UnreadCount);

        var recurring = apiIncident with { Id = Guid.NewGuid().ToString("N"), OccurredAt = recoveredAt + 1 };
        await Observe(snapshot with { CheckedAt = recoveredAt + 1, Incidents = [recurring] });
        var newest = (await first.GetFromJsonAsync<AdminNotificationsPage>(Endpoint + "?pageSize=3"))!;
        Assert.Equal(4, newest.Total);
        Assert.Equal(1, newest.UnreadCount);
        Assert.Equal(3, newest.Items.Count);
        Assert.Null(newest.Items[0].ReadAtUtc);
        Assert.Null(newest.Items[0].HandledAtUtc);

        AdminNotification[] extra = [.. Enumerable.Range(10, 5).Select(offset => new AdminNotification
        {
            Kind = "system", Service = "api", Description = "Unread preview test",
            OccurredAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(recoveredAt + offset)
        })];
        await using (var db = database.CreateContext())
        {
            db.AdminNotifications.AddRange(extra);
            await db.SaveChangesAsync();
        }
        var previewQuery = "?pageSize=1&includeLatestUnread=true";
        using var readNewest = await first.PostAsync(Endpoint + "/" + extra[^1].Id + "/read" + previewQuery, null);
        readNewest.EnsureSuccessStatusCode();
        var withPreview = (await readNewest.Content.ReadFromJsonAsync<AdminNotificationsPage>())!;
        Assert.NotNull(Assert.Single(withPreview.Items).ReadAtUtc);
        Assert.Equal(extra.Take(4).Reverse().Select(x => x.Id), withPreview.LatestUnreadItems!.Select(x => x.Id));
        var otherUnread = (await second.GetFromJsonAsync<AdminNotificationsPage>(Endpoint + "?pageSize=4&unreadOnly=true"))!;
        Assert.Equal(extra[^1].Id, otherUnread.Items[0].Id);
        using var allReadPreview = await first.PostAsync(Endpoint + "/read-all" + previewQuery, null);
        allReadPreview.EnsureSuccessStatusCode();
        var cleared = (await allReadPreview.Content.ReadFromJsonAsync<AdminNotificationsPage>())!;
        Assert.Equal(0, cleared.UnreadCount);
        Assert.Equal(9, cleared.Total);
        Assert.NotNull(Assert.Single(cleared.Items).ReadAtUtc);

        async Task<HttpClient> Login(string email)
        {
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-FocusTime-Admin", "1");
            (await client.PostAsJsonAsync("/api/v1/admin/session", new LoginRequest(email, Password,
                new(Guid.NewGuid(), "Notification test", "Web", "1")))).EnsureSuccessStatusCode();
            return client;
        }
    }
}
