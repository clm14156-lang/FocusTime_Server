using System.ComponentModel.DataAnnotations;

namespace FocusTime.Api.Modules.Sessions.Contracts;

public sealed record DeviceRegistration(Guid InstallationId,
    [param: Required, MaxLength(128)] string Name,
    [param: Required, MaxLength(64)] string OperatingSystem,
    [param: Required, MaxLength(32)] string ClientVersion);
public sealed record DeviceResponse(Guid Id, string Name, string OperatingSystem, string ClientVersion,
    DateTimeOffset FirstSeenAtUtc, DateTimeOffset LastSeenAtUtc, bool IsCurrentDevice,
    IReadOnlyList<SessionResponse> Sessions);
public sealed record SessionResponse(Guid Id, DateTimeOffset CreatedAtUtc, DateTimeOffset LastSeenAtUtc,
    DateTimeOffset ExpiresAtUtc, bool IsCurrentSession);
