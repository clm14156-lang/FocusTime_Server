namespace FocusTime.Api.Modules.Membership;

public sealed class MembershipOrder
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid RequestId { get; set; }
    public string Plan { get; set; } = "";
    public string PlanName { get; set; } = "";
    public int? DurationDays { get; set; }
    public int? DurationMonths { get; set; }
    public long? PlanVersion { get; set; }
    public int AmountFen { get; set; }
    public string Method { get; set; } = "";
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? PaidAtUtc { get; set; }
    public string ActivationStatus { get; set; } = "notStarted";
    public Guid? MembershipGrantId { get; set; }
    public DateTimeOffset? ActivatedAtUtc { get; set; }
    public DateTimeOffset? ActivationAttemptedAtUtc { get; set; }
    public string? ActivationErrorCode { get; set; }
    public string? PaymentProvider { get; set; }
    public string? PaymentReference { get; set; }
}

public sealed class MembershipOrderEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string Kind { get; set; } = "";
    public string Actor { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}
