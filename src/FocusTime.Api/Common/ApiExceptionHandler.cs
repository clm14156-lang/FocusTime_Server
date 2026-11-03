using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using FocusTime.Api.Modules.Identity;

namespace FocusTime.Api.Common;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            return true;
        if (exception is AuthFault fault)
        {
            var problem = new ProblemDetails { Status = fault.Status, Title = fault.Message };
            ApiProblems.Enrich(context, problem);
            problem.Extensions["code"] = fault.Code;
            context.Response.StatusCode = fault.Status;
            context.Response.Headers.CacheControl = "no-store";
            if (fault.Status == 429) context.Response.Headers.RetryAfter =
                (fault.RetryAfterSeconds ?? 60).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await context.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null,
                "application/problem+json", cancellationToken);
            return true;
        }
        var status = exception is BadHttpRequestException badRequest
            ? badRequest.StatusCode : StatusCodes.Status500InternalServerError;
        if (status >= 500)
            logger.LogError(exception, "Unhandled API exception. TraceId: {TraceId}", context.TraceIdentifier);
        await ApiProblems.WriteAsync(context, status,
            status >= 500 ? "An unexpected error occurred." : null, cancellationToken);
        return true;
    }
}
