using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Membership;
using FocusTime.Api.Modules.Sessions;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Admin;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminOrderRefundRequest(Guid OperationId, [param: Range(1, 100000000)] int AmountFen,
    [param: Required, RegularExpression("^(within-guarantee|duplicate-payment|wrong-purchase|service-issue|other)$")] string ReasonCode,
    [param: MaxLength(200)] string? Note, bool RevokeMembership);
public sealed record AdminOrderRefundReceipt(Guid Id, int AmountFen, string ReasonCode, string? Note,
    bool RevokeMembership, bool MembershipRevoked, string Status, string ProviderReference,
    DateTimeOffset CreatedAtUtc, DateTimeOffset CompletedAtUtc);
public sealed record AdminOrderRefundInfo(DateTimeOffset? DeadlineAtUtc, bool Eligible, bool Supported,
    bool CanRevokeMembership, string? BlockReason, AdminOrderRefundReceipt? Receipt);

public sealed class AdminOrderRefundService(AppDbContext db, MembershipService memberships, TimeProvider clock, IHostEnvironment environment)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, string> Reasons = new Dictionary<string, string> {
        ["within-guarantee"] = "用户申请无理由退款（3天内）", ["duplicate-payment"] = "重复支付",
        ["wrong-purchase"] = "购买错误", ["service-issue"] = "服务问题", ["other"] = "其他原因" };
    private bool Supports(MembershipOrder order) => order.PaymentProvider == "simulation" &&
        !string.IsNullOrWhiteSpace(order.PaymentReference) && (environment.IsDevelopment() || environment.IsEnvironment("Testing"));
    public AdminOrderRefundInfo Describe(MembershipOrder order, MembershipOrderRefund? refund, MembershipGrant? grant, DateTimeOffset now)
    {
        var deadline = order.PaidAtUtc?.AddHours(72);
        var inPeriod = order.PaidAtUtc.HasValue && now >= order.PaidAtUtc && now <= deadline;
        var supported = Supports(order);
        var eligible = order.Status == "paid" && refund is null && inPeriod;
        var reason = order.Status == "refunded" || refund is not null ? "order_already_refunded" : order.Status == "refunding" ? "order_refunding" :
            order.Status != "paid" ? "order_not_paid" : !order.PaidAtUtc.HasValue || now < order.PaidAtUtc ? "invalid_payment_time" :
            !inPeriod ? "refund_period_expired" : !supported ? "refund_provider_not_supported" : null;
        var receipt = refund is null ? null : new AdminOrderRefundReceipt(refund.Id, refund.AmountFen, refund.ReasonCode, refund.Note,
            refund.RevokeMembership, refund.RevokedGrantId.HasValue, refund.Status, refund.ProviderReference, refund.CreatedAtUtc, refund.CompletedAtUtc);
        return new(deadline, eligible, supported, grant is not null && grant.UserId == order.UserId && grant.RevokedAtUtc is null, reason, receipt);
    }

    public async Task Refund(Guid orderId, AdminOrderRefundRequest request, HttpContext context, CancellationToken ct)
    {
        if (request.OperationId == Guid.Empty || request.AmountFen <= 0 || !Reasons.ContainsKey(request.ReasonCode) || request.Note?.Length > 200 || request.Note?.Contains('\0') == true)
            throw new AuthFault(400, "invalid_refund_request", "Invalid refund request.");
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var actorId = Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var binding = await (from order in db.MembershipOrders.AsNoTracking() join user in db.Users.AsNoTracking() on order.UserId equals user.Id
            where order.Id == orderId select new { order.UserId, Email = user.Email! }).SingleOrDefaultAsync(ct)
            ?? throw new AuthFault(404, "order_not_found", "Order not found.");
        var actor = await db.Users.AsNoTracking().SingleAsync(x => x.Id == actorId, ct);
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.Length == 0) authorization = "Bearer " + context.Request.Cookies[AdminAccess.CookieName];
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || authorization.Length != 71)
            throw new AuthFault(401, "session_expired", "Please sign in again.");
        var hash = AuthSecurity.Hash(JsonSerializer.Serialize(new { orderId, actorId, request.AmountFen, request.ReasonCode, note, request.RevokeMembership }, Json));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var email in new[] { actor.Email!, binding.Email }.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
            await AccountTransactions.LockAsync(db, email, ct);
        await AccountTransactions.LockSessionAsync(db, clock, context.User, authorization, ct);
        // All grant writers use the same account lock; concurrent retries and refunds serialize here.
        var existing = await db.MembershipOrderRefunds.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.OperationId, ct);
        if (existing is not null)
        {
            if (existing.OrderId != orderId || existing.ActorUserId != actorId || existing.RequestHash != hash)
                throw new AuthFault(409, "refund_operation_conflict", "This operation ID belongs to a different refund request.");
            await transaction.CommitAsync(ct);
            return;
        }
        var purchase = await db.MembershipOrders.SingleAsync(x => x.Id == orderId, ct);
        var prior = await db.MembershipOrderRefunds.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
        MembershipGrant? grant = purchase.MembershipGrantId.HasValue
            ? await db.MembershipGrants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == purchase.MembershipGrantId, ct) : null;
        var now = clock.GetUtcNow();
        var info = Describe(purchase, prior, grant, now);
        if (!info.Eligible || !info.Supported)
            throw new AuthFault(409, info.BlockReason ?? "refund_unavailable", "This order cannot be refunded.");
        if (request.AmountFen > purchase.AmountFen)
            throw new AuthFault(400, "refund_amount_exceeds_payment", "Refund amount cannot exceed the payment.");
        var revokeMembership = request.RevokeMembership || request.AmountFen == purchase.AmountFen;
        if (grant is not null && grant.UserId != purchase.UserId || revokeMembership && purchase.MembershipGrantId.HasValue && grant is null)
            throw new AuthFault(409, "refund_grant_binding_invalid", "The order grant binding cannot be verified.");
        if (revokeMembership && grant is not null && await db.MembershipOrders.AsNoTracking()
            .AnyAsync(x => x.Id != purchase.Id && x.MembershipGrantId == grant.Id, ct))
            throw new AuthFault(409, "refund_grant_binding_invalid", "The membership grant is shared by another order.");
        var refund = new MembershipOrderRefund { Id = request.OperationId, OrderId = purchase.Id, ActorUserId = actorId,
            RequestHash = hash, AmountFen = request.AmountFen, ReasonCode = request.ReasonCode, Note = note,
            RevokeMembership = revokeMembership, ProviderReference = $"SIMREF{request.OperationId:N}", CreatedAtUtc = now, CompletedAtUtc = now };
        // This adapter handles persisted simulation payments only. No provider call or real-money success is fabricated.
        purchase.Status = "refunded";
        db.MembershipOrderRefunds.Add(refund);
        var orderActor = "admin:" + actorId;
        db.MembershipOrderEvents.Add(new() { OrderId = purchase.Id, Kind = "refund_requested", Actor = orderActor, OccurredAtUtc = now });
        db.MembershipOrderEvents.Add(new() { OrderId = purchase.Id, Kind = "refund_completed", Actor = orderActor, OccurredAtUtc = now });
        if (revokeMembership && grant is not null && grant.RevokedAtUtc is null)
        {
            var revokeOperation = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"order-refund:{request.OperationId:N}"))[..16]);
            await memberships.StageChangeAsync(purchase.UserId, revokeOperation, "revoke", "", DateTimeOffset.UnixEpoch, null,
                grant.Id, Reasons[request.ReasonCode] + (note is null ? "" : "：" + note), "admin-refund:" + actorId, ct);
            refund.RevokedGrantId = grant.Id;
            db.MembershipOrderEvents.Add(new() { OrderId = purchase.Id, Kind = "membership_revoked", Actor = orderActor, OccurredAtUtc = now });
        }
        var changes = new List<AdminAuditChange> {
            new("订单状态", "已支付", "已退款"), new("模拟退款金额", "¥0.00", "¥" + (request.AmountFen / 100m).ToString("0.00", CultureInfo.InvariantCulture)),
            new("退款原因", null, Reasons[request.ReasonCode])
        };
        if (refund.RevokedGrantId.HasValue) changes.Add(new("订单关联权益", "未撤销", "已撤销"));
        db.AdminOperationLogs.Add(new() { ActorUserId = actorId, Administrator = actor.Email!, OccurredAtUtc = now,
            Category = "orders", Action = "记录模拟订单退款", TargetId = purchase.Id.ToString(), Target = $"FT{purchase.Id:N}".ToUpperInvariant(),
            Source = "后台 · 订单管理", Ip = context.Connection.RemoteIpAddress?.ToString(), Status = "success",
            ChangesJson = JsonSerializer.Serialize(changes, Json) });
        // Refund receipt, order state, exact-order grant revocation, version and audits all commit or roll back together.
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
