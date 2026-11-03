using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Sessions.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Sessions;

public sealed class DeviceSessionService(AppDbContext db, TimeProvider clock)
{
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
    private async Task<AppUser> LockCallerAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct)
            ?? throw new AuthFault(401, "invalid_session", "Please sign in again.");
        await AccountTransactions.LockAsync(db, user.Email!, ct);
        await db.Entry(user).ReloadAsync(ct);
        if (!await db.LoginSessions.AnyAsync(x => x.Id == sessionId && x.UserId == userId &&
            x.RevokedAtUtc == null && x.AccessExpiresAtUtc > Now && x.ExpiresAtUtc > Now &&
            x.SecurityStamp == user.SecurityStamp, ct))
            throw new AuthFault(401, "invalid_session", "Please sign in again.");
        return user;
    }

    public async Task<IReadOnlyList<DeviceResponse>> ListAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await LockCallerAsync(userId, sessionId, ct);
        var sessions = await db.LoginSessions.AsNoTracking().Where(x => x.UserId == userId &&
            x.RevokedAtUtc == null && x.ExpiresAtUtc > Now && x.SecurityStamp == user.SecurityStamp)
            .OrderByDescending(x => x.LastSeenAtUtc).ThenBy(x => x.Id).ToListAsync(ct);
        var deviceIds = sessions.Select(x => x.DeviceId).Distinct().ToArray();
        var devices = await db.LoginDevices.AsNoTracking().Where(x => x.UserId == userId && deviceIds.Contains(x.Id))
            .ToListAsync(ct);
        var currentDevice = sessions.Single(x => x.Id == sessionId).DeviceId;
        var response = devices.Select(device =>
        {
            var active = sessions.Where(x => x.DeviceId == device.Id).ToArray();
            var seen = active.Max(x => x.LastSeenAtUtc);
            return new DeviceResponse(device.Id, device.Name, device.OperatingSystem, device.ClientVersion,
                device.CreatedAtUtc, seen > device.LastSeenAtUtc ? seen : device.LastSeenAtUtc,
                device.Id == currentDevice, active.Select(x => new SessionResponse(x.Id, x.CreatedAtUtc,
                    x.LastSeenAtUtc, x.ExpiresAtUtc, x.Id == sessionId)).ToArray());
        }).OrderByDescending(x => x.IsCurrentDevice).ThenByDescending(x => x.LastSeenAtUtc).ThenBy(x => x.Id).ToArray();
        await tx.CommitAsync(ct);
        return response;
    }

    public async Task RevokeAsync(Guid userId, Guid callerSessionId, Guid? deviceId, Guid? targetSessionId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockCallerAsync(userId, callerSessionId, ct);
        if (deviceId is { } device && !await db.LoginDevices.AnyAsync(x => x.Id == device && x.UserId == userId, ct) ||
            targetSessionId is { } target && !await db.LoginSessions.AnyAsync(x => x.Id == target && x.UserId == userId, ct))
            throw new AuthFault(404, "not_found", "Device or session was not found.");
        var query = db.LoginSessions.Where(x => x.UserId == userId && x.RevokedAtUtc == null);
        if (deviceId is not null) query = query.Where(x => x.DeviceId == deviceId);
        if (targetSessionId is not null) query = query.Where(x => x.Id == targetSessionId);
        await query.ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAtUtc, Now), ct);
        await tx.CommitAsync(ct);
    }
}
