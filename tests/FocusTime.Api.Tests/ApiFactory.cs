using FocusTime.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FocusTime.Api.Modules.Identity;

namespace FocusTime.Api.Tests;

public sealed class ApiFactory(string? database = null, int permitLimit = 120, string environment = "Development")
    : WebApplicationFactory<Program>
{
    public TestMail Mail { get; } = new();
    public TestClock Clock { get; } = new();
    public string? Secret { get; init; } = Convert.ToBase64String(new byte[32]);
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RateLimiting:PermitLimit"] = permitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["RateLimiting:WindowSeconds"] = "60",
                ["Authentication:Secret"] = Secret
            }));
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            services.AddDbContext<AppDbContext>(options => options.UseNpgsql(database ??
                "Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Timeout=1;Command Timeout=1",
                postgres => postgres.MigrationsHistoryTable("__ef_migrations_history", "public")));
            services.AddControllers().AddApplicationPart(typeof(ProbeController).Assembly);
            services.AddSingleton<IEmailSender>(Mail);
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}

public sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}

public sealed class TestMail : IEmailSender
{
    public bool Fail { get; set; }
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> Codes { get; } = new();
    public Task SendCodeAsync(string email, string purpose, string code, CancellationToken cancellationToken)
    {
        if (Fail) throw new AuthFault(503, "email_unavailable", "Email delivery failed.");
        Codes[email.ToUpperInvariant() + ":" + purpose] = code;
        return Task.CompletedTask;
    }
    public string Code(string email, string purpose = "register") => Codes[email.ToUpperInvariant() + ":" + purpose];
}
