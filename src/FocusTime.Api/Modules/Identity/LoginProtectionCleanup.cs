using FocusTime.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Identity;

internal sealed class LoginProtectionCleanup(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<LoginProtectionCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = clock.GetUtcNow();
                await db.LoginCaptchas.Where(challenge => challenge.ExpiresAtUtc < now.AddHours(-1))
                    .ExecuteDeleteAsync(stoppingToken);
                await db.LoginAttempts.Where(attempt => attempt.WindowStartedAtUtc < now.AddDays(-1))
                    .ExecuteDeleteAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(error, "Expired login protection records could not be removed.");
            }
        }
    }
}
