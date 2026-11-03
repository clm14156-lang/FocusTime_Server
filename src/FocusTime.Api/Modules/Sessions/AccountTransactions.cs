using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FocusTime.Api.Infrastructure.Persistence;
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
}
