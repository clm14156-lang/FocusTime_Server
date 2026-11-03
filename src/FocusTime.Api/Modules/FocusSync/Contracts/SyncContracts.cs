using System.ComponentModel.DataAnnotations;
using FocusTime.Api.Common;

namespace FocusTime.Api.Modules.FocusSync.Contracts;

public enum FocusCompletionKind { Natural, EarlyEnd }
public enum SyncOperationKind { Upsert, Delete }
public enum SyncOperationStatus { Applied, Duplicate, Conflict, Rejected }

public sealed record SubTaskSnapshot(string Title, bool IsCompleted);
public sealed record TaskDetailsSnapshot(string Description, IReadOnlyList<SubTaskSnapshot> SubTasks);
public sealed record CompletedTaskSnapshot(string TaskId, string TaskNameSnapshot, int SortOrder,
    DateTimeOffset? CompletedAtUtc, TaskDetailsSnapshot? Details);

// Completed history only. These snapshots never recreate or mutate live client tasks/rules.
public sealed record FocusRecordPayload(
    int ConfiguredSeconds,
    int ActualSeconds,
    DateTimeOffset PreparationStartedAtUtc,
    DateTimeOffset? FocusStartedAtUtc,
    DateTimeOffset? PlannedEndAtUtc,
    DateTimeOffset CompletedAtUtc,
    FocusCompletionKind CompletionKind,
    bool IsForcedMode,
    string? TargetId,
    string? TargetNameSnapshot,
    bool BlockingEnabled,
    Guid? AutomaticRuleId,
    DateTimeOffset? AutomaticOccurrenceStartedAtUtc,
    IReadOnlyList<CompletedTaskSnapshot> CompletedTasks);

public sealed record SyncPushOperation(Guid OperationId, Guid RecordId, SyncOperationKind Kind,
    FocusRecordPayload? Record, long? BaseRevision);
public sealed record SyncPushRequest(
    [param: Required, MinLength(1), MaxLength(ApiLimits.MaximumSyncBatchSize)]
    IReadOnlyList<SyncPushOperation> Operations);
public sealed record SyncOperationResult(Guid OperationId, Guid RecordId, SyncOperationStatus Status,
    long? ServerRevision, string? Code);
public sealed record SyncPushResponse(IReadOnlyList<SyncOperationResult> Results);
public sealed record FocusRecordChange(Guid RecordId, Guid SourceDeviceId, long ServerRevision,
    bool IsDeleted, FocusRecordPayload? Record);
public sealed record SyncChangesResponse(IReadOnlyList<FocusRecordChange> Changes, string NextCursor, bool HasMore);
public sealed record PageResponse<T>(IReadOnlyList<T> Items, string? NextCursor);
