using System.Globalization;
using System.Text.Json;

namespace FocusTime.Api.Modules.Membership;

public static class MembershipAdmin
{
    public static async Task RunAsync(string[] args, IServiceProvider provider)
    {
        var offset = Array.IndexOf(args, "--membership-admin");
        var action = args.ElementAtOrDefault(offset + 1) ?? "";
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = offset + 2; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 == args.Length || !options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Expected unique --option value pairs.");
        }
        string Need(string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing {key}.");
        var user = Guid.Parse(Need("--user-id")); var operation = Guid.Parse(Need("--operation-id"));
        // Explicit start makes a retried command's idempotency hash stable.
        var starts = action == "grant" ? DateTimeOffset.Parse(Need("--starts-at"), CultureInfo.InvariantCulture).ToUniversalTime() : DateTimeOffset.UnixEpoch;
        var expires = options.TryGetValue("--expires-at", out var date) ? DateTimeOffset.Parse(date, CultureInfo.InvariantCulture).ToUniversalTime() : (DateTimeOffset?)null;
        var grant = options.TryGetValue("--grant-id", out var id) ? Guid.Parse(id) : (Guid?)null;
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<MembershipService>().ChangeAsync(user, operation, action,
            options.GetValueOrDefault("--kind", ""), starts, expires, grant, Need("--reason"), options.GetValueOrDefault("--actor", Environment.UserName));
        Console.WriteLine(JsonSerializer.Serialize(new { result.UserId, result.OperationId, result.GrantId, result.Version, result.Action }));
    }
}
