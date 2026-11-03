using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FocusTime.Api.Modules.FocusSync.Contracts;

namespace FocusTime.Api.Modules.FocusSync;

public static class FocusPayloadCodec
{
    public static readonly JsonSerializerOptions Json = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }
    public static string? Validate(FocusRecordPayload r)
    {
        if (r.ConfiguredSeconds is < 1 or > 86400 || r.ActualSeconds < 0 || r.ActualSeconds > r.ConfiguredSeconds)
            return "invalid_duration";
        if (r.CompletedAtUtc < r.PreparationStartedAtUtc ||
            r.FocusStartedAtUtc is { } start && start < r.PreparationStartedAtUtc ||
            r.FocusStartedAtUtc is { } focus && r.PlannedEndAtUtc is { } end && end < focus)
            return "invalid_time_range";
        // Forced sessions ended during preparation retain their planned future focus-start time.
        if (r.FocusStartedAtUtc > r.CompletedAtUtc &&
            !(r.IsForcedMode && r.CompletionKind == FocusCompletionKind.EarlyEnd && r.ActualSeconds == 0))
            return "invalid_time_range";
        if (r.TargetId?.Length > 128 || r.TargetNameSnapshot?.Length > 4096 ||
            r.CompletedTasks is null || r.CompletedTasks.Count > 200) return "snapshot_too_large";
        // jsonb does not accept the NUL character. Reject this item before saving the batch.
        if (r.TargetId?.Contains('\0') == true || r.TargetNameSnapshot?.Contains('\0') == true)
            return "invalid_snapshot_text";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in r.CompletedTasks)
        {
            if (task is null || string.IsNullOrWhiteSpace(task.TaskId) || task.TaskId.Length > 128 ||
                !ids.Add(task.TaskId) || task.SortOrder < 0 || task.TaskNameSnapshot is null ||
                task.TaskNameSnapshot.Length > 4096) return "invalid_task_snapshot";
            if (task.TaskId.Contains('\0') || task.TaskNameSnapshot.Contains('\0')) return "invalid_snapshot_text";
            if (task.Details is { } details && (details.Description is null || details.Description.Length > 16384 ||
                details.SubTasks is null || details.SubTasks.Count > 200 ||
                details.SubTasks.Any(x => x is null || x.Title is null || x.Title.Length > 4096)))
                return "invalid_task_snapshot";
            if (task.Details is { } text && (text.Description.Contains('\0') || text.SubTasks.Any(x => x.Title.Contains('\0'))))
                return "invalid_snapshot_text";
        }
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(r, Json)) > 262144) return "snapshot_too_large";
        return null;
    }
    public static FocusRecordPayload Normalize(FocusRecordPayload r) => r with
    {
        PreparationStartedAtUtc = r.PreparationStartedAtUtc.ToUniversalTime(),
        FocusStartedAtUtc = r.FocusStartedAtUtc?.ToUniversalTime(),
        PlannedEndAtUtc = r.PlannedEndAtUtc?.ToUniversalTime(),
        CompletedAtUtc = r.CompletedAtUtc.ToUniversalTime(),
        AutomaticOccurrenceStartedAtUtc = r.AutomaticOccurrenceStartedAtUtc?.ToUniversalTime(),
        CompletedTasks = r.CompletedTasks.OrderBy(x => x.SortOrder).ThenBy(x => x.TaskId, StringComparer.Ordinal)
            .Select(x => x with { CompletedAtUtc = (x.CompletedAtUtc ?? r.CompletedAtUtc).ToUniversalTime() }).ToArray()
    };
}
