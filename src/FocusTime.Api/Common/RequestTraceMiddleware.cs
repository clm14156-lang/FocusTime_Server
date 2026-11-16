using System.Diagnostics;
using System.Diagnostics.Metrics;
using FocusTime.Api.Modules.Admin;

namespace FocusTime.Api.Common;

public sealed class RequestTraceMiddleware(RequestDelegate next, ILogger<RequestTraceMiddleware> logger,
    IWebHostEnvironment environment, IConfiguration configuration, AdminAlertSignals alerts)
{
    private static readonly Meter Meter = new("FocusTime.Api.Http");
    private static readonly Counter<long> BackgroundRequests = Meter.CreateCounter<long>("focustime.background.requests");
    private readonly bool logAll = environment.IsDevelopment() && configuration.GetValue<bool>("RequestTracing:LogAll");

    public async Task InvokeAsync(HttpContext context)
    {
        context.TraceIdentifier = Guid.NewGuid().ToString("N");
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
            return Task.CompletedTask;
        });
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["TraceId"] = context.TraceIdentifier });
        var started = Stopwatch.GetTimestamp();
        try { await next(context); }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var path = context.Request.Path;
            var status = context.Response.StatusCode;
            if (!context.RequestAborted.IsCancellationRequested)
                alerts.RecordRequest(context.Request.Method, path.Value, status, context.TraceIdentifier);
            var probe = path == "/health/live" || path == "/health/ready" || path == "/health/alert-collector";
            var sync = path.StartsWithSegments("/api/v1/sync");
            if (probe || sync)
                BackgroundRequests.Add(1, new("operation", probe ? "probe" : "sync"), new("http.response.status_code", status));

            if (logAll || !(probe || sync) || status >= 400 || status != 101 && elapsed >= 500 ||
                (!probe && Random.Shared.Next(100) == 0))
                logger.LogInformation("HTTP {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds} ms",
                    context.Request.Method, path.Value, status, elapsed);
        }
    }
}
