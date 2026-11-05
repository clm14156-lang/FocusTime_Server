using FocusTime.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Membership;

[ApiController, Route("api/v1/membership-plans"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MembershipPlansController(AppDbContext db, IHostEnvironment environment) : ControllerBase
{
    [HttpGet]
    public async Task<object> Get(CancellationToken ct) => new
    {
        plans = (await db.MembershipPlans.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct)).Select(MembershipPlanMapping.ToResponse),
        simulationAvailable = environment.IsDevelopment() || environment.IsEnvironment("Testing")
    };
}
