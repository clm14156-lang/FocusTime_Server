namespace FocusTime.Api.Modules.Identity;

public sealed class EmailChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
    public bool Consumed { get; set; }
    public string? TicketHash { get; set; }
    public DateTimeOffset? TicketExpiresAtUtc { get; set; }
    public bool TicketConsumed { get; set; }
}

public sealed class LoginDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid InstallationId { get; set; }
    public string Name { get; set; } = "";
    public string OperatingSystem { get; set; } = "";
    public string ClientVersion { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
}

public sealed class LoginSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public string SecurityStamp { get; set; } = "";
    public string AccessHash { get; set; } = "";
    public DateTimeOffset AccessExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public sealed class RefreshCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset? UsedAtUtc { get; set; }
    public Guid? RequestId { get; set; }
    public Guid? SuccessorId { get; set; }
}
