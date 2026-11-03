using System.Text.Json;
using FocusTime.Api.Modules.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace FocusTime.Api.Modules.FocusSync;

internal sealed record FocusCursor(Guid UserId, string Purpose, long Position, long? UpperRevision);

internal static class FocusCursorCodec
{
    public static FocusCursor Read(AuthSecurity security, Guid userId, string purpose, string? cursor)
    {
        if (cursor is null) return new(userId, purpose, 0, null);
        try
        {
            if (cursor.Length is < 1 or > 2048) throw Invalid();
            var parts = cursor.Split('.');
            if (parts.Length != 2 || !AuthSecurity.Equal(security.Derive("focus-cursor-v1:" + parts[0]), parts[1])) throw Invalid();
            var value = JsonSerializer.Deserialize<FocusCursor>(WebEncoders.Base64UrlDecode(parts[0]));
            if (value is null || value.UserId != userId || value.Purpose != purpose || value.Position < 0 ||
                value.UpperRevision is { } upper && upper < value.Position) throw Invalid();
            return value;
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException)
        { throw Invalid(); }
    }

    public static string Write(AuthSecurity security, FocusCursor value)
    {
        var body = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(value));
        return body + "." + security.Derive("focus-cursor-v1:" + body);
    }

    public static AuthFault Invalid() => new(400, "invalid_sync_cursor", "The synchronization cursor is invalid for this account or query.");
}
