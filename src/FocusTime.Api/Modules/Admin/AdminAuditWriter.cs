using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Membership;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Admin;

public sealed class AdminAuditWriter(AppDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task StagePlanChange(HttpContext context, MembershipPlanResponse before, MembershipPlan after, CancellationToken ct)
    {
        var actor = Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var email = await db.Users.Where(x => x.Id == actor).Select(x => x.Email).SingleAsync(ct);
        static string Money(int amount) => "¥" + (amount / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        static string Days(int? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) + " 天" : "永久有效";
        var changes = new[] {
            new AdminAuditChange("套餐价格", Money(before.AmountFen), Money(after.AmountFen)),
            new AdminAuditChange("会员时长", Days(before.DurationDays), Days(after.DurationDays)),
            new AdminAuditChange("销售状态", before.Available ? "上架中" : "已下架", after.Available ? "上架中" : "已下架"),
            new AdminAuditChange("推荐状态", before.Recommended ? "推荐" : "不推荐", after.Recommended ? "推荐" : "不推荐"),
            new AdminAuditChange("套餐版本", before.Version.ToString(CultureInfo.InvariantCulture), after.Version.ToString(CultureInfo.InvariantCulture))
        }.Where(change => change.Before != change.After).ToArray();
        db.AdminOperationLogs.Add(new() { ActorUserId = actor, Administrator = email ?? actor.ToString(),
            OccurredAtUtc = clock.GetUtcNow(), Category = "plans", Action = "修改会员套餐", TargetId = after.Id,
            Target = after.Name, Source = "后台 · 套餐管理", Ip = context.Connection.RemoteIpAddress?.ToString(),
            Status = "success", ChangesJson = JsonSerializer.Serialize(changes, Json) });
        // Caller commits this audit and the plan update in the same transaction.
    }

    public async Task SaveFailure(HttpContext context, string category, string action, string targetId, string failureCode)
    {
        var actor = Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var email = await db.Users.Where(x => x.Id == actor).Select(x => x.Email).SingleOrDefaultAsync();
        var target = targetId;
        if (category == "users" && Guid.TryParse(targetId, out var userId))
            target = await db.Users.Where(x => x.Id == userId).Select(x => x.Email).SingleOrDefaultAsync() ?? targetId;
        if (category == "plans") target = targetId switch { "monthly" => "月会员", "annual" => "年会员", "lifetime" => "永久会员", _ => "未知套餐" };
        if (category == "orders" && Guid.TryParse(targetId, out var orderId)) target = $"FT{orderId:N}".ToUpperInvariant();
        db.AdminOperationLogs.Add(new() { ActorUserId = actor, Administrator = email ?? actor.ToString(),
            OccurredAtUtc = clock.GetUtcNow(), Category = category, Action = action, TargetId = targetId,
            Target = target, Source = "后台 · " + (category == "users" ? "用户管理" : category == "plans" ? "套餐管理" : "订单管理"),
            Ip = context.Connection.RemoteIpAddress?.ToString(), Status = "failure", FailureCode = failureCode });
        await db.SaveChangesAsync();
    }
}
