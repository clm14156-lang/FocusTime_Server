namespace FocusTime.Api.Modules.Admin;

// Deliberately excludes passwords, hashes, tokens and request bodies.
public sealed class AdminSecurityAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ActorUserId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string Operation { get; set; } = "admin.password.change";
    public string Source { get; set; } = "admin-web";
    public string Result { get; set; } = "";
    public string? FailureCode { get; set; }
}
