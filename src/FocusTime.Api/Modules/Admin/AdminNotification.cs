namespace FocusTime.Api.Modules.Admin;

public sealed class AdminNotification
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Kind { get; init; }
    public Guid? OrderId { get; init; }
    public string? Service { get; init; }
    public Guid? MonitorIncidentId { get; set; }
    public required string Description { get; set; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public DateTimeOffset? HandledAtUtc { get; set; }
    public string? AlertKey { get; set; }
    public DateTimeOffset? LastOccurredAtUtc { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public string? Resolution { get; set; }
    public string? AuditId { get; init; }
}

public sealed class AdminNotificationRead
{
    public Guid AdministratorId { get; init; }
    public Guid NotificationId { get; init; }
    public DateTimeOffset ReadAtUtc { get; init; }
}
