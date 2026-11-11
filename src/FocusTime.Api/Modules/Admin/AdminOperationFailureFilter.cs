using FocusTime.Api.Modules.Identity;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace FocusTime.Api.Modules.Admin;

// Only authenticated, authorized business mutations are audited. Reads and previews that succeed are excluded.
public sealed class AdminOperationFailureFilter(AdminAccess access, IServiceScopeFactory scopes,
    ILogger<AdminOperationFailureFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var descriptor = context.ActionDescriptor as ControllerActionDescriptor;
        var operation = (descriptor?.ControllerName, descriptor?.ActionName) switch {
            ("AdminMembershipPlans", "Update") => ("plans", "修改会员套餐", "id"),
            ("AdminMembershipAdjustments", "Apply") => ("users", "调整用户会员", "userId"),
            ("AdminMembershipAdjustments", "Preview") => ("users", "校验会员调整方案", "userId"),
            ("AdminOrders", "Retry") => ("orders", "重试发放订单权益", "id"),
            ("AdminOrders", "Refund") => ("orders", "处理订单退款", "id"),
            ("AdminUserActions", "Disable") => ("users", AdminUserActionsController.DisableAction, "userId"),
            ("AdminUserActions", "Restore") => ("users", AdminUserActionsController.RestoreAction, "userId"),
            ("AdminUserActions", "SignOut") => ("users", AdminUserActionsController.SignOutAction, "userId"),
            _ => default
        };
        if (operation.Item1 is null || !access.Allows(context.HttpContext.User)) { await next(); return; }
        ActionExecutedContext executed;
        try { executed = await next(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await Record(error, 500);
            throw;
        }
        var status = (executed.Result as IStatusCodeActionResult)?.StatusCode ?? 200;
        if (executed.Exception is not OperationCanceledException && (executed.Exception is not null || status >= 400))
            await Record(executed.Exception, status);

        async Task Record(Exception? error, int resultStatus)
        {
            var code = error is AuthFault fault ? fault.Code : error is not null ? "internal_error" : resultStatus == 400 ? "validation_failed" : "request_rejected";
            var target = Convert.ToString(context.RouteData.Values.GetValueOrDefault(operation.Item3)) ?? "";
            if (target.Length > 128) target = target[..128];
            try
            {
                // The failed business transaction has unwound; use a fresh context so no partial changes are saved.
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AdminAuditWriter>()
                    .SaveFailure(context.HttpContext, operation.Item1, operation.Item2, target, code);
            }
            catch (Exception auditError) { logger.LogError(auditError, "Unable to persist failed administrator operation audit. TraceId: {TraceId}", context.HttpContext.TraceIdentifier); }
        }
    }
}
