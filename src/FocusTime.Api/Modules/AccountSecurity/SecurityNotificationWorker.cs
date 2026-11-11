using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.AccountSecurity;

public sealed class SecurityNotificationDispatcher(AppDbContext db, IEmailSender mail, TimeProvider clock)
{
    public async Task<bool> DispatchOneAsync(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var item = await db.SecurityNotifications.FromSqlInterpolated($"SELECT * FROM identity.security_notifications WHERE \"CompletedAtUtc\" IS NULL AND \"NextAttemptAtUtc\" <= {now} ORDER BY \"CreatedAtUtc\" LIMIT 1 FOR UPDATE SKIP LOCKED")
            .SingleOrDefaultAsync(ct);
        if (item is null) return false;
        if (item.ExpiresAtUtc > now)
        {
            item.Attempts++;
            try { await mail.SendSecurityNoticeAsync(item.Recipient, item.Subject, item.Body, ct); item.Delivered = true; }
            catch (AuthFault fault) when (fault.Code == "email_unavailable")
            { item.NextAttemptAtUtc = now.AddSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(7, item.Attempts)))); }
        }
        if (item.Delivered || item.ExpiresAtUtc <= now)
        { item.CompletedAtUtc = now; item.Recipient = ""; item.Body = ""; }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return true;
    }
}

public sealed class SecurityNotificationWorker(IServiceScopeFactory scopes, ILogger<SecurityNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<SecurityNotificationDispatcher>();
                for (var i = 0; i < 20 && await dispatcher.DispatchOneAsync(stoppingToken); i++) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { logger.LogWarning("Security notification delivery deferred; the durable queue will retry."); }
        }
    }
}
