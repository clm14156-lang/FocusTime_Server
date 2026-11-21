using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FocusTime.Api.Modules.Identity.Contracts;
using FocusTime.Api.Modules.Membership;
using FocusTime.Api.Modules.Membership.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FocusTime.Api.Tests;

public sealed class MembershipTests
{
    private sealed class Scenario : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required ApiFactory Factory { get; init; }
        public required HttpClient Client { get; init; }
        public required TokenResponse Token { get; init; }
        public static async Task<Scenario> Create()
        {
            var database = await TestDatabase.CreateAsync();
            await using var db = database.CreateContext(); await db.Database.MigrateAsync();
            var factory = new ApiFactory(database.ConnectionString); var client = factory.CreateClient();
            const string email = "member@example.test";
            var challenge = await (await client.PostAsJsonAsync("/api/v1/auth/registration-code", new { email })).Content.ReadFromJsonAsync<EmailChallengeResponse>();
            (await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, challenge!.ChallengeId, factory.Mail.Code(email), "Member123456"))).EnsureSuccessStatusCode();
            var token = (await (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Member123456", new(Guid.NewGuid(), "A", "Windows", "1"))))
                .Content.ReadFromJsonAsync<TokenResponse>())!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            return new() { Database = database, Factory = factory, Client = client, Token = token };
        }
        public async Task<MembershipAudit> Change(string kind = "lifetime", DateTimeOffset? expiry = null, Guid? operation = null,
            string action = "grant", Guid? grant = null, DateTimeOffset? start = null)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<MembershipService>().ChangeAsync(Token.User.Id,
                operation ?? Guid.NewGuid(), action, action == "grant" ? kind : "", start ?? DateTimeOffset.UnixEpoch,
                expiry, grant, "test operation", "test-admin");
        }
        public async Task<MembershipResponse> Status() => (await Client.GetFromJsonAsync<MembershipResponse>("/api/v1/me/membership",
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }))!;
        public async Task<EntitlementLeaseResponse> Lease() => (await (await Client.PostAsJsonAsync("/api/v1/me/entitlement-lease",
            new EntitlementLeaseRequest(new('a', 64), new('b', 64), new('c', 64), MaxLeaseDays: 7))).Content.ReadFromJsonAsync<EntitlementLeaseResponse>())!;
        public async ValueTask DisposeAsync() { Client.Dispose(); await Factory.DisposeAsync(); await Database.DisposeAsync(); }
    }
    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task EffectiveMembership_ExposesVerifiedTrialProvenance_WithoutChangingEntitlements()
    {
        await using var s = await Scenario.Create();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(s.Factory.Clock.GetUtcNow().ToUnixTimeMilliseconds());
        var expires = now.AddDays(7);
        var gift = await s.Change("annual", expires, start: now);
        Assert.False((await s.Status()).IsTrial); // A short remaining duration does not prove trial provenance.
        await using (var db = s.Database.CreateContext())
        {
            db.AdminMembershipAdjustments.Add(new()
            {
                UserId = s.Token.User.Id, ActorUserId = s.Token.User.Id, OperationId = Guid.NewGuid(),
                Action = "grant", Plan = "annual", State = "applied", ReasonCode = "trial", GrantId = gift.GrantId,
                CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(5), AppliedAtUtc = now
            });
            await db.SaveChangesAsync();
        }
        var trial = await s.Status();
        Assert.True(trial.IsTrial);
        Assert.Equal("annual", trial.Plan);
        Assert.Equal(MembershipKind.Annual, trial.Kind);
        Assert.True(trial.IsActive);
        Assert.Equal(now, trial.StartsAtUtc);
        Assert.Equal(expires, trial.ExpiresAtUtc);
        var paid = await CreateOrder(s.Client, "monthly", Guid.NewGuid());
        (await s.Client.PostAsJsonAsync($"{OrderPath}/{paid.Id}/simulate-payment", new { })).EnsureSuccessStatusCode();
        var purchased = await s.Status();
        Assert.False(purchased.IsTrial);
        Assert.Equal("monthly", purchased.Plan);
        await s.Change("lifetime");
        var lifetime = await s.Status();
        Assert.False(lifetime.IsTrial);
        Assert.Null(lifetime.ExpiresAtUtc);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task OrderHistory_UsesLinkedGrantExpiry_IncludingRenewalsAndLifetime()
    {
        await using var s = await Scenario.Create();
        var monthly = await CreateOrder(s.Client, "monthly", Guid.NewGuid());
        (await s.Client.PostAsJsonAsync($"{OrderPath}/{monthly.Id}/simulate-payment", new { })).EnsureSuccessStatusCode();
        var annual = await CreateOrder(s.Client, "annual", Guid.NewGuid());
        (await s.Client.PostAsJsonAsync($"{OrderPath}/{annual.Id}/simulate-payment", new { })).EnsureSuccessStatusCode();
        var history = (await s.Client.GetFromJsonAsync<MembershipOrderResponse[]>(OrderPath))!;
        var annualEntry = Assert.Single(history, x => x.Id == annual.Id);
        await using var db = s.Database.CreateContext();
        var grant = await db.MembershipGrants.SingleAsync(x => x.Id == annualEntry.MembershipGrantId);
        Assert.Equal(grant.ExpiresAtUtc, annualEntry.MembershipExpiresAtUtc);
        Assert.NotEqual(annualEntry.ExpiresAtUtc, annualEntry.MembershipExpiresAtUtc);
        Assert.True(annualEntry.MembershipExpiresAtUtc > annualEntry.PaidAtUtc!.Value.AddDays(annualEntry.DurationDays!.Value));
        var lifetime = await CreateOrder(s.Client, "lifetime", Guid.NewGuid());
        (await s.Client.PostAsJsonAsync($"{OrderPath}/{lifetime.Id}/simulate-payment", new { })).EnsureSuccessStatusCode();
        var updated = (await s.Client.GetFromJsonAsync<MembershipOrderResponse[]>(OrderPath))!;
        var lifetimeEntry = Assert.Single(updated, x => x.Id == lifetime.Id);
        Assert.Equal("activated", lifetimeEntry.ActivationStatus);
        Assert.NotNull(lifetimeEntry.MembershipGrantId);
        Assert.Null(lifetimeEntry.MembershipExpiresAtUtc);
    }

    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    private static JsonElement Payload(string token) => JsonSerializer.Deserialize<JsonElement>(Decode(token.Split('.')[1]));

    private const string OrderPath = "/api/v1/me/membership-orders";
    private static async Task<MembershipOrderResponse> CreateOrder(HttpClient client, string plan, Guid requestId)
    {
        using var response = await client.PostAsJsonAsync(OrderPath,
            new { plan, method = "wechat", requestId, amountFen = 1 });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MembershipOrderResponse>())!;
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task WebsiteOrders_PersistAndGrantOnce_AndWindowsReadsTheSameMembership()
    {
        await using var s = await Scenario.Create();
        var requestId = Guid.NewGuid();
        var monthly = await CreateOrder(s.Client, "monthly", requestId);
        Assert.Equal("pending", monthly.Status);
        Assert.Equal(1500, monthly.AmountFen);
        Assert.Equal(monthly.Id, (await CreateOrder(s.Client, "monthly", requestId)).Id);
        await using var db = s.Database.CreateContext();
        Assert.Equal(s.Token.User.Id, (await db.MembershipOrders.AsNoTracking().SingleAsync()).UserId);
        Assert.Empty(await db.MembershipGrants.ToListAsync());
        (await s.Client.PostAsJsonAsync($"{OrderPath}/{monthly.Id}/method", new { method = "alipay" })).EnsureSuccessStatusCode();

        var paid = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            using var response = await s.Client.PostAsync($"{OrderPath}/{monthly.Id}/simulate-payment", null);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<MembershipPaymentResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }))!;
        }));
        Assert.All(paid, result =>
        {
            Assert.Equal("paid", result.Order.Status);
            Assert.Equal("alipay", result.Order.Method);
            Assert.NotNull(result.Order.PaidAtUtc);
            Assert.Equal("monthly", result.Membership.Plan);
            Assert.Equal(MembershipKind.Annual, result.Membership.Kind);
            Assert.Equal(1, result.Membership.Version);
            Assert.Equal(result.Membership.StartsAtUtc!.Value.AddDays(30).ToUnixTimeSeconds(), result.Membership.ExpiresAtUtc!.Value.ToUnixTimeSeconds());
        });
        Assert.Single(await db.MembershipGrants.ToListAsync());
        Assert.Single(await db.MembershipAudits.ToListAsync());
        Assert.Equal("paid", (await db.MembershipOrders.AsNoTracking().SingleAsync()).Status);
        var lease = Payload((await s.Lease()).Token);
        Assert.Equal("annual", lease.GetProperty("kind").GetString());
        Assert.Equal(paid[0].Membership.ExpiresAtUtc!.Value.ToUnixTimeSeconds(), lease.GetProperty("grantExp").GetInt64());
        Assert.Equal(2, lease.GetProperty("rights").GetArrayLength());

        var annual = await CreateOrder(s.Client, "annual", Guid.NewGuid());
        Assert.Equal(9800, annual.AmountFen);
        (await s.Client.PostAsync($"{OrderPath}/{annual.Id}/simulate-payment", null)).EnsureSuccessStatusCode();
        Assert.Equal(paid[0].Membership.ExpiresAtUtc!.Value.AddDays(365).ToUnixTimeSeconds(), (await s.Status()).ExpiresAtUtc!.Value.ToUnixTimeSeconds());
        var lifetime = await CreateOrder(s.Client, "lifetime", Guid.NewGuid());
        Assert.Equal(19800, lifetime.AmountFen);
        (await s.Client.PostAsync($"{OrderPath}/{lifetime.Id}/simulate-payment", null)).EnsureSuccessStatusCode();
        Assert.Equal("lifetime", (await s.Status()).Plan);
        Assert.Null((await s.Status()).ExpiresAtUtc);
        var login = await (await s.Client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(s.Token.User.Email, "Member123456", new(Guid.NewGuid(), "B", "Windows", "1")))).Content.ReadFromJsonAsync<TokenResponse>();
        Assert.Equal("lifetime", login!.User.Membership);
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task WebsiteSimulation_RejectsAnonymousForeignExpiredAndProductionRequests()
    {
        await using var s = await Scenario.Create();
        var order = await CreateOrder(s.Client, "monthly", Guid.NewGuid());
        var path = $"{OrderPath}/{order.Id}/simulate-payment";
        using var anonymous = s.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsync($"{OrderPath}/{Guid.NewGuid()}/simulate-payment", null)).StatusCode);

        const string otherEmail = "other-order@example.test";
        var code = await (await anonymous.PostAsJsonAsync("/api/v1/auth/registration-code", new { email = otherEmail })).Content.ReadFromJsonAsync<EmailChallengeResponse>();
        (await anonymous.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(otherEmail, code!.ChallengeId,
            s.Factory.Mail.Code(otherEmail), "Other123456"))).EnsureSuccessStatusCode();
        var other = await (await anonymous.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(otherEmail,
            "Other123456", new(Guid.NewGuid(), "C", "Windows", "1")))).Content.ReadFromJsonAsync<TokenResponse>();
        anonymous.DefaultRequestHeaders.Authorization = new("Bearer", other!.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsync(path, null)).StatusCode);
        Assert.Empty((await anonymous.GetFromJsonAsync<MembershipOrderResponse[]>(OrderPath))!);

        await using var production = new ApiFactory(s.Database.ConnectionString, environment: "Production");
        using var productionClient = production.CreateClient();
        productionClient.DefaultRequestHeaders.Authorization = new("Bearer", s.Token.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await productionClient.PostAsync(path, null)).StatusCode);

        await using var db = s.Database.CreateContext();
        await db.MembershipOrders.Where(x => x.Id == order.Id).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.CreatedAtUtc, s.Factory.Clock.GetUtcNow().AddMinutes(-16))
            .SetProperty(x => x.ExpiresAtUtc, s.Factory.Clock.GetUtcNow().AddMinutes(-1)));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync(path, null)).StatusCode);
        Assert.Equal("expired", (await s.Client.GetFromJsonAsync<MembershipOrderResponse[]>(OrderPath))!.Single().Status);
        Assert.Empty(await db.MembershipGrants.ToListAsync());
    }

    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task NormalUser_HasNoRights_AndNoPublicGrantRoute()
    {
        await using var s = await Scenario.Create(); var status = await s.Status();
        Assert.False(status.IsActive); Assert.Equal(MembershipKind.Normal, status.Kind); Assert.Empty(status.Entitlements);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsJsonAsync("/api/v1/me/membership/grant", new { kind = "lifetime" })).StatusCode);
        Assert.Empty(Payload((await s.Lease()).Token).GetProperty("rights").EnumerateArray());
    }
    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Lifetime_LeaseHasVerifiableSignature_AndSessionDeviceBindings()
    {
        await using var s = await Scenario.Create(); await s.Change(); var lease = await s.Lease();
        var parts = lease.Token.Split('.'); var claims = Payload(lease.Token);
        using var rsa = RSA.Create(); var key = s.Factory.Services.GetRequiredService<EntitlementSigner>().PublicKey;
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.SubjectPublicKeyInfo), out _);
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.False(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1] + "x"), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.Equal(s.Token.User.Id.ToString(), claims.GetProperty("sub").GetString());
        Assert.Equal(s.Token.DeviceId.ToString(), claims.GetProperty("deviceId").GetString());
        Assert.Equal(s.Token.SessionId.ToString(), claims.GetProperty("sessionId").GetString());
        Assert.Equal(new string('b', 64), claims.GetProperty("machineBinding").GetString());
        Assert.Equal(new string('c', 64), claims.GetProperty("nonce").GetString());
        Assert.Equal(2, claims.GetProperty("rights").GetArrayLength());
        Assert.Equal(TimeSpan.FromDays(7), lease.ExpiresAtUtc - s.Factory.Clock.GetUtcNow());
        using var legacyResponse = await s.Client.PostAsJsonAsync("/api/v1/me/entitlement-lease",
            new { apiScope = new string('a', 64), machineBinding = new string('b', 64), nonce = new string('c', 64) });
        var legacy = (await legacyResponse.Content.ReadFromJsonAsync<EntitlementLeaseResponse>())!;
        Assert.Equal(TimeSpan.FromDays(3), legacy.ExpiresAtUtc - s.Factory.Clock.GetUtcNow());
        Assert.Equal("lifetime", (await s.Client.GetFromJsonAsync<UserProfile>("/api/v1/auth/me"))!.Membership);
    }
    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Annual_ClampsToGrantExpiry_AndExpiresWithoutManualRevocation()
    {
        await using var s = await Scenario.Create(); var expiry = s.Factory.Clock.GetUtcNow().AddMinutes(2);
        await s.Change("annual", expiry); var status = await s.Status();
        Assert.Equal(MembershipKind.Annual, status.Kind); Assert.Equal(expiry.ToUnixTimeSeconds(), (await s.Lease()).ExpiresAtUtc.ToUnixTimeSeconds());
        s.Factory.Clock.Advance(TimeSpan.FromMinutes(3));
        var expired = await s.Status();
        Assert.False(expired.IsActive);
        Assert.Equal("expired", expired.State);
        Assert.Equal(expiry.ToUnixTimeSeconds(), expired.LastExpiredAtUtc!.Value.ToUnixTimeSeconds());
        var proof = Payload((await s.Lease()).Token);
        Assert.Equal("expired", proof.GetProperty("state").GetString());
        Assert.Empty(proof.GetProperty("rights").EnumerateArray());
    }
    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task FutureGrant_DoesNotUnlock_AndRenewalSelectsLongestActiveAnnual()
    {
        await using var s = await Scenario.Create(); var now = s.Factory.Clock.GetUtcNow();
        await s.Change(start: now.AddDays(1)); Assert.False((await s.Status()).IsActive);
        await s.Change("annual", now.AddDays(2)); await s.Change("annual", now.AddDays(5));
        var status = await s.Status(); Assert.Equal(MembershipKind.Annual, status.Kind);
        Assert.Equal(now.AddDays(5).ToUnixTimeSeconds(), status.ExpiresAtUtc!.Value.ToUnixTimeSeconds());
    }
    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Revoke_IncrementsVersion_ClearsRights_AndPreservesAudit()
    {
        await using var s = await Scenario.Create(); var a = await s.Change(); await s.Change(action: "revoke", grant: a.GrantId);
        var status = await s.Status(); Assert.False(status.IsActive); Assert.Equal(2, status.Version);
        Assert.Equal(2, Payload((await s.Lease()).Token).GetProperty("version").GetInt64());
        await using var db = s.Database.CreateContext(); Assert.Equal(2, await db.MembershipAudits.CountAsync());
        Assert.NotNull((await db.MembershipGrants.SingleAsync()).RevokedAtUtc);
    }
    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task AdminRetry_IsIdempotent_AndRejectsOperationReuse()
    {
        await using var s = await Scenario.Create(); var id = Guid.NewGuid();
        var a = await s.Change(operation: id); var b = await s.Change(operation: id);
        Assert.Equal(a.GrantId, b.GrantId); Assert.Equal(1, (await s.Status()).Version);
        await Assert.ThrowsAsync<ArgumentException>(() => s.Change("annual", s.Factory.Clock.GetUtcNow().AddDays(1), id));
        await using var db = s.Database.CreateContext(); Assert.Single(await db.MembershipGrants.ToListAsync());
    }
    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task RevokedSession_CannotGetNewLease_AndAnonymousKeysContainOnlyPublicMaterial()
    {
        await using var s = await Scenario.Create(); await s.Change();
        (await s.Client.DeleteAsync($"/api/v1/me/sessions/{s.Token.SessionId}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.PostAsJsonAsync("/api/v1/me/entitlement-lease", new EntitlementLeaseRequest(new('a', 64), new('b', 64), new('c', 64)))).StatusCode);
        s.Client.DefaultRequestHeaders.Authorization = null;
        var keys = await s.Client.GetStringAsync("/api/v1/entitlement-keys");
        Assert.Contains("subjectPublicKeyInfo", keys); Assert.DoesNotContain("PRIVATE", keys);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync("/api/v1/me/membership")).StatusCode);
    }
    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task Lease_ClampsToSessionAbsoluteExpiry_AndRejectsMalformedBinding()
    {
        await using var s = await Scenario.Create(); await s.Change();
        await using var db = s.Database.CreateContext(); var end = s.Factory.Clock.GetUtcNow().AddHours(1);
        await db.LoginSessions.Where(x => x.Id == s.Token.SessionId).ExecuteUpdateAsync(x => x.SetProperty(v => v.ExpiresAtUtc, end));
        Assert.Equal(end.ToUnixTimeSeconds(), (await s.Lease()).ExpiresAtUtc.ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync("/api/v1/me/entitlement-lease", new { apiScope = "bad", machineBinding = "", nonce = "" })).StatusCode);
    }
    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task ConcurrentAdminRetries_ProduceOneGrantOneAuditOneVersion()
    {
        await using var s = await Scenario.Create(); var operation = Guid.NewGuid();
        var changes = await Task.WhenAll(s.Change(operation: operation), s.Change(operation: operation), s.Change(operation: operation));
        Assert.All(changes, x => Assert.Equal(1, x.Version));
        await using var db = s.Database.CreateContext(); Assert.Single(await db.MembershipGrants.ToListAsync());
        Assert.Single(await db.MembershipAudits.ToListAsync());
    }
    [PostgresFact, Trait("Category", "PostgreSQL")]
    public async Task UserCannotChooseAnotherUsersMembership_AndDatabaseRejectsAnnualWithoutExpiry()
    {
        await using var s = await Scenario.Create(); await s.Change();
        const string email = "other-member@example.test";
        var code = await (await s.Client.PostAsJsonAsync("/api/v1/auth/registration-code", new { email })).Content.ReadFromJsonAsync<EmailChallengeResponse>();
        (await s.Client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, code!.ChallengeId, s.Factory.Mail.Code(email), "Other123456"))).EnsureSuccessStatusCode();
        var token = (await (await s.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "Other123456", new(Guid.NewGuid(), "B", "Windows", "1"))))
            .Content.ReadFromJsonAsync<TokenResponse>())!;
        s.Client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        Assert.False((await s.Status()).IsActive);
        var lease = await s.Lease(); Assert.Equal(token.User.Id.ToString(), Payload(lease.Token).GetProperty("sub").GetString());
        Assert.Empty(Payload(lease.Token).GetProperty("rights").EnumerateArray());
        await using var db = s.Database.CreateContext();
        db.MembershipGrants.Add(new() { Id = Guid.NewGuid(), UserId = token.User.Id, Kind = "annual", StartsAtUtc = DateTimeOffset.UtcNow, CreatedAtUtc = DateTimeOffset.UtcNow });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, Assert.IsType<Npgsql.PostgresException>(error.InnerException).SqlState);
    }
}
