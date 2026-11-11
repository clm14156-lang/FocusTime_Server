namespace FocusTime.Api.Modules.Admin;

public sealed class AdminNotification
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Kind { get; init; }
    public Guid? OrderId { get; init; }
    public string? Service { get; init; }
    public Guid? MonitorIncidentId { get; set; }
    public required string Description { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public DateTimeOffset? HandledAtUtc { get; set; }
}

public sealed class AdminNotificationRead
{
    public Guid AdministratorId { get; init; }
    public Guid NotificationId { get; init; }
    public DateTimeOffset ReadAtUtc { get; init; }
}
