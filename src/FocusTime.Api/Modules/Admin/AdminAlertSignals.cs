namespace FocusTime.Api.Modules.Admin;

internal sealed record AlertSample(string Key, string Kind, string Service, string Description,
    DateTimeOffset At, int Count, bool Active, Guid? EpisodeId = null, DateTimeOffset? FirstAt = null,
    DateTimeOffset? RecoveredAt = null);

public sealed class AdminAlertSignals(TimeProvider clock, IConfiguration configuration)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> ApiRoutes = [
        "POST /api/v1/auth/login", "POST /api/v1/auth/register", "POST /api/v1/auth/registration-code",
        "POST /api/v1/admin/session", "GET /api/v1/me/membership", "POST /api/v1/me/membership-orders"
    ];
    private readonly object gate = new();
    private readonly DateTimeOffset startedAt = clock.GetUtcNow();
    private readonly string instance = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(configuration["AdminAlerts:InstanceId"] ?? Environment.MachineName)))[..12];
    private readonly Dictionary<(string Route, long Bucket), RequestBucket> requests = [];
    private readonly Dictionary<string, MailWindow> mail = [];
    private readonly Queue<LoginFailure> logins = [];
    private sealed record RequestBucket(int Total, int Failed, string? Trace);
    private sealed record LoginFailure(string Source, string Account, DateTimeOffset At);
    private sealed class MailWindow
    {
        public DateTimeOffset? LastSuccess { get; set; }
        public Queue<DateTimeOffset> Failures { get; } = new();
        public List<AlertSample> Episodes { get; } = [];
    }

    public void RecordRequest(string method, string? path, int status, string traceId)
    {
        var route = method + " " + path?.TrimEnd('/').ToLowerInvariant();
        if (!ApiRoutes.Contains(route)) return;
        var bucket = Bucket(clock.GetUtcNow());
        lock (gate)
        {
            foreach (var old in requests.Keys.Where(x => x.Bucket < bucket - 2).ToArray()) requests.Remove(old);
            var key = (route, bucket);
            var current = requests.GetValueOrDefault(key) ?? new(0, 0, null);
            var failed = status >= 500;
            requests[key] = new(current.Total + 1, current.Failed + (failed ? 1 : 0), failed ? traceId : current.Trace);
        }
    }

    public void RecordRegistrationMail(bool success)
    {
        var channel = configuration["Email:Mode"]?.ToLowerInvariant() switch
        {
            "pickup" => "Pickup",
            "log" => "Log",
            _ => "SMTP"
        };
        lock (gate)
        {
            if (!mail.TryGetValue(channel, out var current)) mail[channel] = current = new();
            var now = clock.GetUtcNow();
            if (success)
            {
                current.LastSuccess = now;
                current.Failures.Clear();
                if (current.Episodes.LastOrDefault() is { RecoveredAt: null } episode)
                    current.Episodes[^1] = episode with { RecoveredAt = now };
            }
            else
            {
                while (current.Failures.TryPeek(out var at) && at < now - Window) current.Failures.Dequeue();
                current.Failures.Enqueue(now);
                if (current.Failures.Count > 3) current.Failures.Dequeue();
                if (current.Episodes.LastOrDefault() is { RecoveredAt: null } episode)
                    current.Episodes[^1] = episode with { At = now, Count = episode.Count + 1 };
                else if (current.Failures.Count == 3)
                    current.Episodes.Add(new($"mail:{instance}:{channel}", "mail", channel,
                        $"{channel} 注册验证码连续发送失败。", now, 3, true, Guid.NewGuid(), current.Failures.Peek()));
            }
        }
    }

    public void RecordLoginFailure(string sourceHash, string accountHash)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            TrimLogins(now);
            if (logins.Count >= 8192) logins.Dequeue();
            logins.Enqueue(new(sourceHash, accountHash, now));
        }
    }

    internal string LoginPrefix => $"login:{instance}:";
    internal bool HasFullLoginWindow(DateTimeOffset now) => now >= startedAt + Window;

    internal AlertSample[] Evaluate(DateTimeOffset now)
    {
        lock (gate)
        {
            var samples = new List<AlertSample>();
            var bucket = Bucket(now);
            var end = DateTimeOffset.FromUnixTimeSeconds(bucket * (long)Window.TotalSeconds);
            foreach (var route in ApiRoutes)
            {
                var latest = requests.GetValueOrDefault((route, bucket - 1));
                var prior = requests.GetValueOrDefault((route, bucket - 2));
                if (latest is null || latest.Total < 50) continue;
                var active = IsBad(latest) && IsBad(prior);
                if (!active && IsBad(latest)) continue;
                samples.Add(new($"rate:{instance}:{route}", "api-rate", "api",
                    $"{route} 最近 5 分钟 {latest.Failed}/{latest.Total} 次请求返回 5xx。追踪 ID {latest.Trace}",
                    end, latest.Failed, active));
            }
            foreach (var (channel, window) in mail)
            {
                window.Episodes.RemoveAll(x => x.RecoveredAt < now.AddHours(-24));
                samples.AddRange(window.Episodes);
                if (window.LastSuccess is { } success)
                    samples.Add(new($"mail:{instance}:{channel}", "mail", channel, "", success, 0, false));
            }
            TrimLogins(now);
            foreach (var group in logins.GroupBy(x => x.Source))
            {
                var count = group.Count();
                var accounts = group.Select(x => x.Account).Distinct().Count();
                if (count >= 20 && accounts >= 5)
                    samples.Add(new(LoginPrefix + group.Key, "login", "authentication",
                        $"来源 {group.Key[..12]} 在 5 分钟内登录失败 {count} 次，涉及 {accounts} 个账号。",
                        group.Last().At, count, true));
            }
            return samples.ToArray();
        }
    }

    private static long Bucket(DateTimeOffset time) => time.ToUnixTimeSeconds() / (long)Window.TotalSeconds;
    private static bool IsBad(RequestBucket? bucket) => bucket is { Total: >= 50 } && bucket.Failed * 20L >= bucket.Total;
    private void TrimLogins(DateTimeOffset now)
    {
        while (logins.TryPeek(out var failure) && failure.At < now - Window) logins.Dequeue();
    }
}
