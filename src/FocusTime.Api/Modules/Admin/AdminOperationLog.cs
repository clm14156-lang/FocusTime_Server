namespace FocusTime.Api.Modules.Admin;

// Append-only business audit. Never stores request bodies, passwords, cookies or tokens.
public sealed class AdminOperationLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ActorUserId { get; set; }
    public string Administrator { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string Category { get; set; } = "";
    public string Action { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string Target { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Ip { get; set; }
    public string Status { get; set; } = "";
    public string? FailureCode { get; set; }
    public string ChangesJson { get; set; } = "[]";
}
public sealed record AdminAuditChange(string Field, string? Before, string? After);
