using System.ComponentModel.DataAnnotations;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Membership;
using FocusTime.Api.Modules.Sessions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Admin;

public sealed record UpdateMembershipPlanRequest([param: Range(1, 100000000)] int AmountFen,
    [param: Range(1, 36500)] int? DurationDays, bool Available, bool Recommended,
    [param: Range(1, long.MaxValue)] long ExpectedVersion);

[ApiController, Authorize(Policy = AdminAccess.Policy), Route("api/v1/admin/membership-plans"),
    ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminMembershipPlansController(AppDbContext db, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<MembershipPlanResponse>> List(CancellationToken ct) =>
        (await db.MembershipPlans.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct))
            .Select(MembershipPlanMapping.ToResponse).ToArray();

    [HttpPut("{id}")]
    public async Task<ActionResult<MembershipPlanResponse>> Update(string id, UpdateMembershipPlanRequest request, CancellationToken ct)
    {
        if (Request.Headers["X-FocusTime-Admin"] != "1") return StatusCode(403);
        if (id is not ("monthly" or "annual" or "lifetime"))
            throw new AuthFault(404, "plan_not_found", "Membership plan not found.");
        if (id == "lifetime" ? request.DurationDays is not null : request.DurationDays is null)
            throw new AuthFault(400, "invalid_plan_duration", "Timed plans require days; lifetime plans must not expire.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.Length == 0) authorization = "Bearer " + Request.Cookies[AdminAccess.CookieName];
        var session = await AccountTransactions.LockSessionAsync(db, clock, User, authorization, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM membership.plans WHERE \"Id\" = {id} FOR UPDATE", ct);
        var plan = await db.MembershipPlans.SingleAsync(x => x.Id == id, ct);
        if (plan.Version != request.ExpectedVersion)
            throw new AuthFault(409, "plan_version_conflict", "The plan changed. Reload before saving.");
        plan.AmountFen = request.AmountFen;
        plan.DurationDays = request.DurationDays;
        plan.Available = request.Available;
        plan.Recommended = request.Recommended;
        plan.Version++;
        plan.UpdatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
        plan.UpdatedByUserId = session.UserId;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return MembershipPlanMapping.ToResponse(plan);
    }
}
