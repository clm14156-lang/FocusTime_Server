using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace FocusTime.Api.Common;

public static class ApiProblems
{
    public static void Enrich(HttpContext context, ProblemDetails problem)
    {
        var status = problem.Status ?? StatusCodes.Status500InternalServerError;
        problem.Instance ??= context.Request.Path;
        problem.Extensions.TryAdd("code", status switch
        {
            400 => "validation_failed",
            401 => "authentication_required",
            403 => "permission_denied",
            404 => "not_found",
            405 => "method_not_allowed",
            409 => "conflict",
            413 => "payload_too_large",
            415 => "unsupported_media_type",
            429 => "rate_limit_exceeded",
            503 => "service_unavailable",
            _ => "internal_error"
        });
        problem.Extensions["traceId"] = context.TraceIdentifier;
    }

    public static async Task WriteAsync(HttpContext context, int status, string? title = null,
        CancellationToken cancellationToken = default)
    {
        context.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title ?? ReasonPhrases.GetReasonPhrase(status)
        };
        Enrich(context, problem);
        var service = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        if (!await service.TryWriteAsync(new ProblemDetailsContext
            { HttpContext = context, ProblemDetails = problem }))
            await context.Response.WriteAsJsonAsync(problem, options: (System.Text.Json.JsonSerializerOptions?)null,
                contentType: "application/problem+json",
                cancellationToken: cancellationToken);
    }
}
