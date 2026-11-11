namespace FocusTime.Api.Modules.Admin;

// Durable preview + idempotency receipt. Applied grant receipts are the authoritative gift provenance.
public sealed class AdminMembershipAdjustment
{
    public Guid UserId { get; set; }
    public Guid OperationId { get; set; }
    public Guid ActorUserId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Plan { get; set; }
    public string? Scope { get; set; }
    public string ReasonCode { get; set; } = "";
    public string? Note { get; set; }
    public string State { get; set; } = "preview";
    public string ExpectedFingerprint { get; set; } = "";
    public string BeforeJson { get; set; } = "{}";
    public string AfterJson { get; set; } = "{}";
    public string TargetGrantIdsJson { get; set; } = "[]";
    public Guid? GrantId { get; set; }
    public DateTimeOffset? GrantStartsAtUtc { get; set; }
    public DateTimeOffset? GrantExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? AppliedAtUtc { get; set; }
    public long? AppliedVersion { get; set; }
}
