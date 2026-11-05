using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Membership.Contracts;
using FocusTime.Api.Modules.Sessions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Membership;

public sealed record CreateMembershipOrderRequest(
    [param: Required, RegularExpression("^(monthly|annual|lifetime)$")] string Plan,
    [param: Required, RegularExpression("^(wechat|alipay)$")] string Method,
    Guid RequestId, [param: Range(1, long.MaxValue)] long? ExpectedPlanVersion = null);
public sealed record MembershipOrderMethodRequest(
    [param: Required, RegularExpression("^(wechat|alipay)$")] string Method);
public sealed record MembershipOrderResponse(Guid Id, string OrderNumber, string Plan, int AmountFen, string Currency,
    string Method, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? PaidAtUtc,
    DateTimeOffset ServerTimeUtc, string ActivationStatus = "notStarted", Guid? MembershipGrantId = null,
    DateTimeOffset? ActivatedAtUtc = null, string? ActivationErrorCode = null, string? PlanName = null,
    int? DurationDays = null, int? DurationMonths = null, long? PlanVersion = null);
public sealed record MembershipPaymentResponse(MembershipOrderResponse Order, MembershipResponse Membership);

[ApiController, Authorize, Route("api/v1/me/membership-orders"), EnableRateLimiting("auth"),
    ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MembershipOrdersController(AppDbContext db, MembershipOrderService orders, TimeProvider clock,
    IHostEnvironment environment) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<MembershipOrderResponse>> List(CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var orders = await db.MembershipOrders.AsNoTracking().Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Take(50).ToListAsync(ct);
        return orders.Select(ToResponse).ToArray();
    }

    [HttpPost]
    public async Task<ActionResult<MembershipOrderResponse>> Create(CreateMembershipOrderRequest request, CancellationToken ct)
    {
        RequireSimulation();
        if (request.RequestId == Guid.Empty)
            throw new AuthFault(400, "invalid_order_request", "Select a plan and provide a request ID.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var session = await LockSessionAsync(ct);
        var existing = await db.MembershipOrders.SingleOrDefaultAsync(x => x.UserId == session.UserId && x.RequestId == request.RequestId, ct);
        if (existing is not null)
        {
            if (existing.Plan != request.Plan) throw new AuthFault(409, "order_request_conflict", "This request ID already belongs to another plan.");
            return ToResponse(existing);
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM membership.plans WHERE \"Id\" = {request.Plan} FOR SHARE", ct);
        var terms = await db.MembershipPlans.SingleOrDefaultAsync(x => x.Id == request.Plan, ct)
            ?? throw new AuthFault(404, "plan_not_found", "Membership plan not found.");
        if (!terms.Available) throw new AuthFault(409, "plan_unavailable", "This plan is not available for new orders.");
        if (request.ExpectedPlanVersion.HasValue && request.ExpectedPlanVersion != terms.Version)
            throw new AuthFault(409, "plan_changed", "The plan changed. Fetch current terms before ordering.");
        var now = clock.GetUtcNow();
        var order = new MembershipOrder { Id = Guid.NewGuid(), UserId = session.UserId, RequestId = request.RequestId,
            Plan = request.Plan, PlanName = terms.Name, AmountFen = terms.AmountFen, DurationDays = terms.DurationDays,
            PlanVersion = terms.Version, Method = request.Method, CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(15) };
        db.MembershipOrders.Add(order);
        orders.Record(order, "order_created", "website");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return StatusCode(201, ToResponse(order));
    }

    [HttpPost("{id:guid}/method")]
    public async Task<MembershipOrderResponse> ChangeMethod(Guid id, MembershipOrderMethodRequest request, CancellationToken ct)
    {
        RequireSimulation();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var session = await LockSessionAsync(ct);
        var order = await FindOrderAsync(id, session.UserId, ct);
        RequirePending(order, clock.GetUtcNow());
        order.Method = request.Method;
        orders.Record(order, "payment_method_changed", "website");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToResponse(order);
    }

    [HttpPost("{id:guid}/simulate-payment")]
    public async Task<MembershipPaymentResponse> Confirm(Guid id, CancellationToken ct)
    {
        RequireSimulation();
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var session = await LockSessionAsync(ct);
            var order = await FindOrderAsync(id, session.UserId, ct);
            if (order.Status != "paid")
            {
                var now = clock.GetUtcNow();
                RequirePending(order, now);
                order.Status = "paid";
                order.PaidAtUtc = now;
                order.PaymentProvider = "simulation";
                order.PaymentReference = $"SIM{order.Id:N}";
                orders.Record(order, "payment_confirmed", "simulation");
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        // Refresh tracked entities after releasing the first lock: another notification may have fulfilled it.
        db.ChangeTracker.Clear();
        return await orders.ActivateAsync(id, "simulation", ct);
    }

    private Task<LoginSession> LockSessionAsync(CancellationToken ct) =>
        AccountTransactions.LockSessionAsync(db, clock, User, Request.Headers.Authorization.ToString(), ct);

    private async Task<MembershipOrder> FindOrderAsync(Guid id, Guid userId, CancellationToken ct) =>
        await db.MembershipOrders.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct)
        ?? throw new AuthFault(404, "order_not_found", "Order not found.");

    private void RequireSimulation()
    {
        if (!SimulationAvailable)
            throw new AuthFault(404, "simulation_unavailable", "Simulated payment is unavailable.");
    }

    private bool SimulationAvailable => environment.IsDevelopment() || environment.IsEnvironment("Testing");

    private static void RequirePending(MembershipOrder order, DateTimeOffset now)
    {
        if (order.Status != "pending") throw new AuthFault(409, "order_not_pending", "The order is no longer pending.");
        if (order.ExpiresAtUtc <= now) throw new AuthFault(409, "order_expired", "The order has expired.");
    }

    private MembershipOrderResponse ToResponse(MembershipOrder order) => MembershipOrderService.ToResponse(order, clock.GetUtcNow());
}
