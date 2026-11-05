namespace FocusTime.Api.Modules.Membership;

public sealed class MembershipGrant
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Kind { get; set; } = "";
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public sealed class MembershipState
{
    public Guid UserId { get; set; }
    public long Version { get; set; }
}

public sealed class MembershipAudit
{
    public Guid UserId { get; set; }
    public Guid OperationId { get; set; }
    public Guid? GrantId { get; set; }
    public string Action { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Actor { get; set; } = "";
    public long Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
