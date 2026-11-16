using System.Text.Json;
using System.Net.Http.Json;

namespace FocusTime.Api.Modules.Admin;

// Runs in its own process. The file and optional webhook remain usable while the API or DB is down.
public sealed class AdminAlertWatchdog(IConfiguration configuration)
{
    private const string FileName = "watchdog.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string? directory = configuration["AdminAlerts:SpoolDirectory"];
    internal bool Enabled => !string.IsNullOrWhiteSpace(directory);

    internal sealed class Snapshot
    {
        public DateTimeOffset CheckedAt { get; set; }
        public int ApiLatencyMs { get; set; }
        public Dictionary<string, ProbeState> Checks { get; init; } = [];
        public List<Incident> Incidents { get; init; } = [];
    }

    internal sealed class ProbeState
    {
        public int Failures { get; set; }
        public int Successes { get; set; }
        public bool Healthy { get; set; }
        public DateTimeOffset? FirstFailure { get; set; }
        public Guid? OpenId { get; set; }
    }

    internal sealed class Incident
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public required string Service { get; init; }
        public required string Description { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public DateTimeOffset LastOccurredAt { get; set; }
        public int Count { get; set; }
        public DateTimeOffset? RecoveredAt { get; set; }
        public bool Sent { get; set; }
        public bool RecoverySent { get; set; }
    }

    internal Snapshot? Read()
    {
        if (!Enabled) return null;
        try
        {
            using var input = File.Open(Path.Combine(directory!, FileName), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<Snapshot>(input, Json);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal Snapshot? ReadForApi()
    {
        try { return Read(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    internal async Task RunAsync(CancellationToken ct)
    {
        if (!Enabled || !TryHttpUrl(configuration["AdminAlerts:PublicBaseUrl"], out var api))
            throw new InvalidOperationException("Set AdminAlerts:SpoolDirectory and AdminAlerts:PublicBaseUrl for the watchdog.");
        var webhookSetting = configuration["AdminAlerts:WebhookUrl"];
        Uri? webhook = null;
        if (!string.IsNullOrWhiteSpace(webhookSetting) && !TryHttpUrl(webhookSetting, out webhook))
            throw new InvalidOperationException("AdminAlerts:WebhookUrl must use HTTPS or local HTTP.");
        Directory.CreateDirectory(directory!);
        using var ownership = File.Open(Path.Combine(directory!, "watchdog.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var snapshot = Read() ?? new Snapshot();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { Timeout = TimeSpan.FromSeconds(4), MaxResponseContentBufferSize = 64 * 1024 };
        using var interval = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            var now = DateTimeOffset.UtcNow;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var live = await ProbeAsync(client, api!, "/health/live", ct);
            snapshot.ApiLatencyMs = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            bool? ready = live ? await ProbeAsync(client, api!, "/health/ready", ct) : null;
            bool? collector = ready == true ? await ProbeAsync(client, api!, "/health/alert-collector", ct) : null;
            snapshot.CheckedAt = now;
            Observe(snapshot, "api", "外部探测无法访问 API。", live, 3, now);
            if (ready.HasValue) Observe(snapshot, "database", "API 可访问，数据库未通过就绪检查。", ready.Value, 3, now);
            if (collector.HasValue) Observe(snapshot, "collector", "通知采集连续 3 分钟未成功。", collector.Value, 1, now);
            snapshot.Incidents.RemoveAll(x => x.RecoveredAt < now.AddDays(-30));
            Save(snapshot);

            if (webhook is not null)
            {
                foreach (var incident in snapshot.Incidents.Where(x => !x.Sent || (x.RecoveredAt.HasValue && !x.RecoverySent)).Take(4))
                {
                    bool recovery = incident.Sent;
                    if (!await DeliverAsync(client, webhook, incident, recovery, ct)) break;
                    if (recovery) incident.RecoverySent = true;
                    else incident.Sent = true;
                    Save(snapshot);
                }
            }
        } while (await interval.WaitForNextTickAsync(ct));
    }

    internal static void Observe(Snapshot snapshot, string service, string description, bool healthy, int threshold, DateTimeOffset now)
    {
        if (!snapshot.Checks.TryGetValue(service, out var check)) snapshot.Checks[service] = check = new();
        check.Healthy = healthy;
        var open = snapshot.Incidents.Find(x => x.Id == check.OpenId);
        if (healthy)
        {
            check.Failures = 0;
            check.FirstFailure = null;
            check.Successes = Math.Min(2, check.Successes + 1);
            if (open is not null && check.Successes == 2)
            {
                open.RecoveredAt = now;
                check.OpenId = null;
            }
            return;
        }

        check.Successes = 0;
        check.FirstFailure ??= now;
        check.Failures++;
        if (open is not null)
        {
            open.Count++;
            open.LastOccurredAt = now;
        }
        else if (check.Failures >= threshold)
        {
            open = new Incident { Service = service, Description = description, OccurredAt = check.FirstFailure.Value,
                LastOccurredAt = now, Count = check.Failures };
            snapshot.Incidents.Add(open);
            check.OpenId = open.Id;
            Console.WriteLine($"Watchdog opened {service} incident {open.Id}.");
        }
    }

    private void Save(Snapshot snapshot)
    {
        var final = Path.Combine(directory!, FileName);
        var temporary = final + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Json));
        File.Move(temporary, final, overwrite: true);
    }

    private static async Task<bool> ProbeAsync(HttpClient client, Uri api, string path, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(new Uri(api, path), ct);
            if (!response.IsSuccessStatusCode) return false;
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String && status.GetString() == "healthy";
        }
        catch (HttpRequestException) { return false; }
        catch (JsonException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    private static async Task<bool> DeliverAsync(HttpClient client, Uri webhook, Incident incident, bool recovery, CancellationToken ct)
    {
        var status = recovery ? "recovered" : "open";
        using var message = new HttpRequestMessage(HttpMethod.Post, webhook);
        message.Headers.Add("Idempotency-Key", $"{incident.Id:N}:{status}");
        message.Content = JsonContent.Create(new { incident.Id, incident.Service, incident.Description,
            incident.OccurredAt, incident.RecoveredAt, incident.Count, Status = status });
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode) return true;
        }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        Console.Error.WriteLine($"Watchdog webhook delivery failed for incident {incident.Id}. It will retry.");
        return false;
    }

    private static bool TryHttpUrl(string? value, out Uri? url) =>
        Uri.TryCreate(value, UriKind.Absolute, out url) && url.UserInfo.Length == 0 &&
        (url.Scheme == Uri.UriSchemeHttps || (url.IsLoopback && url.Scheme == Uri.UriSchemeHttp));
}

public sealed class AdminAlertHeartbeat(TimeProvider clock)
{
    private long lastSuccess = clock.GetUtcNow().ToUnixTimeMilliseconds();
    internal bool IsFresh => clock.GetUtcNow().ToUnixTimeMilliseconds() - Interlocked.Read(ref lastSuccess) < 180_000;
    internal void Complete() => Interlocked.Exchange(ref lastSuccess, clock.GetUtcNow().ToUnixTimeMilliseconds());
}
