namespace FocusTime.Api.Modules.Membership;

// One confirmed refund per order; an operation ID is an immutable, retryable receipt.
public sealed class MembershipOrderRefund
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid ActorUserId { get; set; }
    public string RequestHash { get; set; } = "";
    public int AmountFen { get; set; }
    public string ReasonCode { get; set; } = "";
    public string? Note { get; set; }
    public bool RevokeMembership { get; set; }
    public Guid? RevokedGrantId { get; set; }
    public string Status { get; set; } = "refunded";
    public string ProviderReference { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
}
