using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FocusTime.Api.Infrastructure.Health;

public static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            checks = report.Entries.ToDictionary(entry => entry.Key,
                entry => entry.Value.Status.ToString().ToLowerInvariant()),
            traceId = context.TraceIdentifier
        }, context.RequestAborted);
    }
}
