namespace FocusTime.Api.Modules.FocusSync;

public sealed class FocusRecord
{
    public Guid UserId { get; set; }
    public Guid RecordId { get; set; }
    public Guid SourceDeviceId { get; set; }
    public long Revision { get; set; }
    public bool IsDeleted { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string PayloadHash { get; set; } = "";
    public DateTimeOffset ReceivedAtUtc { get; set; }
}
public sealed class FocusSyncState
{
    public Guid UserId { get; set; }
    public long LastRevision { get; set; }
}
// Immutable snapshots keep a fixed pagination boundary valid while newer deletes commit.
public sealed class FocusChange
{
    public Guid UserId { get; set; }
    public long Revision { get; set; }
    public Guid RecordId { get; set; }
    public Guid SourceDeviceId { get; set; }
    public bool IsDeleted { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset ReceivedAtUtc { get; set; }
}
public sealed class FocusSyncReceipt
{
    public Guid UserId { get; set; }
    public Guid OperationId { get; set; }
    public Guid RecordId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Code { get; set; }
    public long? Revision { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
}
