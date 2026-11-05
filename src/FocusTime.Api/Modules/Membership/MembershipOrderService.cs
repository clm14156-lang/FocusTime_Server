using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Sessions;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Membership;

public sealed class MembershipOrderService(AppDbContext db, MembershipService memberships, TimeProvider clock,
    ILogger<MembershipOrderService> logger)
{
    public void Record(MembershipOrder order, string kind, string actor) => db.MembershipOrderEvents.Add(new()
    { OrderId = order.Id, Kind = kind, Actor = actor, OccurredAtUtc = clock.GetUtcNow() });

    // Payment confirmation is committed before fulfillment. A grant failure must never erase a payment.
    // Future verified provider callbacks can use this fulfillment path after persisting their payment receipt.
    public async Task<MembershipPaymentResponse> ActivateAsync(Guid orderId, string actor, CancellationToken ct)
    {
        var email = await (from purchase in db.MembershipOrders.AsNoTracking()
            join user in db.Users.AsNoTracking() on purchase.UserId equals user.Id
            where purchase.Id == orderId select user.Email).SingleOrDefaultAsync(ct)
            ?? throw new AuthFault(404, "order_not_found", "Order not found.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AccountTransactions.LockAsync(db, email, ct);
        var order = await db.MembershipOrders.SingleAsync(x => x.Id == orderId, ct);
        if (order.Status != "paid") throw new AuthFault(409, "order_not_paid", "Only paid orders can grant membership.");
        if (order.ActivationStatus != "activated")
        {
            await transaction.CreateSavepointAsync("before_membership", ct);
            try
            {
                // One grant ID per order is also a database uniqueness guard against duplicate delivery.
                var grant = await db.MembershipGrants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == order.Id, ct);
                if (grant is not null && (grant.UserId != order.UserId || grant.Kind != order.Plan))
                    throw new InvalidOperationException("The order grant binding is inconsistent.");
                if (grant is null)
                {
                    var now = clock.GetUtcNow();
                    DateTimeOffset? expiry = null;
                    if (order.Plan != "lifetime")
                    {
                        var latest = await db.MembershipGrants.Where(x => x.UserId == order.UserId && x.RevokedAtUtc == null &&
                            x.StartsAtUtc <= now && x.ExpiresAtUtc > now).MaxAsync(x => x.ExpiresAtUtc, ct);
                        // Fulfill the purchased snapshot, even if the catalog changes or is delisted later.
                        var start = latest ?? now;
                        expiry = order.DurationDays is > 0 ? start.AddDays(order.DurationDays.Value) :
                            order.DurationMonths is > 0 ? start.AddMonths(order.DurationMonths.Value) :
                            throw new InvalidOperationException("The order has no valid duration snapshot.");
                    }
                    await memberships.StageChangeAsync(order.UserId, order.Id, "grant", order.Plan, now, expiry, null,
                        "Membership order payment", "membership-order", ct);
                }
                order.ActivationStatus = "activated";
                order.MembershipGrantId = order.Id;
                order.ActivatedAtUtc = clock.GetUtcNow();
                order.ActivationAttemptedAtUtc = order.ActivatedAtUtc;
                order.ActivationErrorCode = null;
                Record(order, "membership_activated", actor);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Roll back partial grants/version/audit changes, then durably record the failure.
                await transaction.RollbackToSavepointAsync("before_membership", ct);
                db.ChangeTracker.Clear();
                order = await db.MembershipOrders.SingleAsync(x => x.Id == orderId, ct);
                order.ActivationStatus = "failed";
                order.ActivationAttemptedAtUtc = clock.GetUtcNow();
                order.ActivationErrorCode = "membership_activation_failed";
                Record(order, "membership_activation_failed", actor);
                await db.SaveChangesAsync(ct);
                logger.LogError(error, "Membership fulfillment failed for order {OrderId}.", orderId);
            }
        }
        var membership = await memberships.GetAsync(order.UserId, ct);
        await transaction.CommitAsync(ct);
        return new(ToResponse(order, clock.GetUtcNow()), membership);
    }

    public static MembershipOrderResponse ToResponse(MembershipOrder order, DateTimeOffset now) => new(order.Id,
        $"FT{order.Id:N}".ToUpperInvariant(), order.Plan, order.AmountFen, "CNY", order.Method,
        order.Status == "pending" && order.ExpiresAtUtc <= now ? "expired" : order.Status,
        order.CreatedAtUtc, order.ExpiresAtUtc, order.PaidAtUtc, now, order.ActivationStatus,
        order.MembershipGrantId, order.ActivatedAtUtc, order.ActivationErrorCode, order.PlanName,
        order.DurationDays, order.DurationMonths, order.PlanVersion);
}
