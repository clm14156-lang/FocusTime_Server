using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Sessions;

public static class AccountTransactions
{
    public static Task LockAsync(AppDbContext db, string email, CancellationToken ct)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().Normalize().ToUpperInvariant()));
        var key = BinaryPrimitives.ReadInt64BigEndian(hash);
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
    }

    public static async Task<LoginSession> LockSessionAsync(AppDbContext db, TimeProvider clock,
        ClaimsPrincipal principal, string authorization, CancellationToken ct)
    {
        var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var sessionId = Guid.Parse(principal.FindFirstValue("sid")!);
        var email = await db.Users.Where(x => x.Id == userId).Select(x => x.Email).SingleOrDefaultAsync(ct)
            ?? throw new AuthFault(401, "session_expired", "Please sign in again.");
        await LockAsync(db, email, ct);
        var now = clock.GetUtcNow();
        var accessHash = AuthSecurity.Hash(authorization[7..].Trim());
        return await (from s in db.LoginSessions.AsNoTracking() join u in db.Users.AsNoTracking() on s.UserId equals u.Id
            where s.Id == sessionId && s.UserId == userId && s.RevokedAtUtc == null && s.ExpiresAtUtc > now &&
                s.AccessHash == accessHash && s.AccessExpiresAtUtc > now && s.SecurityStamp == u.SecurityStamp && u.EmailConfirmed select s)
            .SingleOrDefaultAsync(ct) ?? throw new AuthFault(401, "session_expired", "Please sign in again.");
    }
}
