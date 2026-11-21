using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FocusTime.Api.Modules.Identity.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Tests;

public sealed class LoginCaptchaTests
{
    [PostgresFact]
    public async Task ChallengeIsRequiredAcrossDevicesAndCannotBeBypassedByAnIncorrectAnswer()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        await using var factory = new ApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        const string email = "captcha-login@example.test";
        const string password = "CaptchaTest149";
        const string path = "/api/v1/auth/login";
        var device = new FocusTime.Api.Modules.Sessions.Contracts.DeviceRegistration(Guid.NewGuid(), "Test", "Windows", "1");
        var otherDevice = device with { InstallationId = Guid.NewGuid() };
        using var registrationCode = await client.PostAsJsonAsync("/api/v1/auth/registration-code", new EmailCodeRequest(email));
        var registration = (await registrationCode.Content.ReadFromJsonAsync<EmailChallengeResponse>())!;
        using var created = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email,
            registration.ChallengeId, factory.Mail.Code(email), password));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var initial = new LoginRequest(email, "incorrect", device);
        for (var count = 1; count <= 3; count++)
        {
            using var failure = await client.PostAsJsonAsync(path, initial);
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
            var problem = await failure.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("invalid_credentials", problem.GetProperty("code").GetString());
            Assert.Equal(count == 3, problem.TryGetProperty("captcha", out _));
        }

        var validPassword = initial with { Password = password, Device = otherDevice };
        var required = await ChallengeAsync(validPassword, "captcha_required");
        using var refresh = await client.PostAsJsonAsync(path + "/captcha", new RefreshLoginCaptchaRequest(email, otherDevice.InstallationId));
        refresh.EnsureSuccessStatusCode();
        var refreshed = (await refresh.Content.ReadFromJsonAsync<LoginCaptchaResponse>())!;
        Assert.NotEqual(required, refreshed.ChallengeId);
        var invalid = await ChallengeAsync(validPassword with { CaptchaChallengeId = refreshed.ChallengeId, CaptchaCode = "ZZZZZ" }, "captcha_invalid");
        Assert.NotEqual(refreshed.ChallengeId, invalid);
        Assert.Equal(3, await db.Users.Select(user => user.AccessFailedCount).SingleAsync());
        Assert.False(await db.LoginSessions.AnyAsync());

        for (var count = 0; count < 6; count++) await ChallengeAsync(validPassword, "captcha_required");
        using var limited = await client.PostAsJsonAsync(path, validPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        using var success = await client.PostAsJsonAsync(path, validPassword);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(0, await db.LoginAttempts.Select(attempt => attempt.ConsecutiveFailures).SingleAsync());

        async Task<Guid> ChallengeAsync(LoginRequest request, string expectedCode)
        {
            using var response = await client.PostAsJsonAsync(path, request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(expectedCode, problem.GetProperty("code").GetString());
            var captcha = problem.GetProperty("captcha");
            var image = captcha.GetProperty("image").GetBytesFromBase64();
            Assert.Equal(new byte[] { (byte)'B', (byte)'M' }, image[..2]);
            Assert.False(captcha.TryGetProperty("answer", out _));
            return captcha.GetProperty("challengeId").GetGuid();
        }
    }
}
