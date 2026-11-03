using System.ComponentModel.DataAnnotations;

namespace FocusTime.Api.Common;

public sealed class RequestRateLimitOptions
{
    [Range(1, 100000)]
    public int PermitLimit { get; set; } = 120;
    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}

public static class ApiLimits
{
    public const int MaximumRequestBytes = 1024 * 1024;
    public const int MaximumSyncBatchSize = 200;
}
