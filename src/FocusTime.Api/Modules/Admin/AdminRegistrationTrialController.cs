using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Membership;
using FocusTime.Api.Modules.Sessions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Admin;

public sealed record RegistrationTrialRuleResponse(int DurationDays, long Version);

public sealed record UpdateRegistrationTrialRuleRequest(
    [param: Range(RegistrationTrialRule.MinimumDays, RegistrationTrialRule.MaximumDays)]
    [property: JsonNumberHandling(JsonNumberHandling.Strict)] int DurationDays,
    [param: Range(1, long.MaxValue)] long ExpectedVersion);

[ApiController, Authorize(Policy = AdminAccess.Policy), Route("api/v1/admin/registration-trial"),
    ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminRegistrationTrialController(AppDbContext db, TimeProvider clock, AdminAuditWriter audit) : ControllerBase
{
    [HttpGet]
    public Task<RegistrationTrialRuleResponse> Get(CancellationToken ct) =>
        db.RegistrationTrialRules.AsNoTracking().Where(x => x.Id == RegistrationTrialRule.SingletonId)
            .Select(x => new RegistrationTrialRuleResponse(x.DurationDays, x.Version)).SingleAsync(ct);

    [HttpPut]
    public async Task<ActionResult<RegistrationTrialRuleResponse>> Update(UpdateRegistrationTrialRuleRequest request, CancellationToken ct)
    {
        if (Request.Headers["X-FocusTime-Admin"] != "1") return StatusCode(403);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.Length == 0) authorization = "Bearer " + Request.Cookies[AdminAccess.CookieName];
        await AccountTransactions.LockSessionAsync(db, clock, User, authorization, ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM membership.registration_trial_rule WHERE \"Id\" = {RegistrationTrialRule.SingletonId} FOR UPDATE", ct);
        var rule = await db.RegistrationTrialRules.SingleAsync(x => x.Id == RegistrationTrialRule.SingletonId, ct);
        if (rule.Version != request.ExpectedVersion)
            throw new AuthFault(409, "trial_rule_version_conflict", "The trial rule changed. Reload before saving.");

        if (rule.DurationDays != request.DurationDays)
        {
            await audit.StageTrialRuleChange(HttpContext, rule.DurationDays, request.DurationDays, ct);
            rule.DurationDays = request.DurationDays;
            rule.Version++;
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new RegistrationTrialRuleResponse(rule.DurationDays, rule.Version);
    }
}
