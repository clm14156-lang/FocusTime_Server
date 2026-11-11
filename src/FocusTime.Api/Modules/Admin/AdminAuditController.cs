using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text.Json;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Admin;

public sealed class AdminAuditQuery
{
    [MaxLength(256)] public string? Query { get; set; }
    [RegularExpression("^(all|users|orders|plans|settings)$")] public string Category { get; set; } = "all";
    [RegularExpression("^(all|success|failure)$")] public string Status { get; set; } = "all";
    public DateOnly? Start { get; set; }
    public DateOnly? End { get; set; }
    [Range(1, 1000000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 10;
}
public sealed record AdminAuditResult(string Status, IReadOnlyList<AdminAuditChange> Changes, string? Reason);
public sealed record AdminAuditLog(string Id, DateTimeOffset OccurredAtUtc, string Administrator, string Category,
    string Action, string Target, string TargetId, string Source, string? Ip, AdminAuditResult Result, string? Note);
public sealed record AdminAuditPage(IReadOnlyList<AdminAuditLog> Items, int Total, int Page, int PageSize, DateTimeOffset ServerTimeUtc);
public sealed record AdminAuditRow(string Id, DateTimeOffset OccurredAtUtc, string Administrator, string Category,
    string Action, string Target, string TargetId, string Source, string? Ip, string Status, string? FailureCode,
    string ChangesJson, string? BeforeJson, string? AfterJson, string? Note);

[ApiController, Authorize(Policy = AdminAccess.Policy), Route("api/v1/admin/audit-logs"),
    ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminAuditController(AppDbContext db, TimeProvider clock) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // UNION ALL is over persisted records only. Each source has a distinct stable ID; adjustment child audits and payment automation are excluded.
    private IQueryable<AdminAuditRow> Records() => db.Database.SqlQueryRaw<AdminAuditRow>("""
        SELECT 'operation:' || a."Id"::text AS "Id", a."OccurredAtUtc", a."Administrator", a."Category", a."Action",
            a."Target", a."TargetId", a."Source", a."Ip", a."Status", a."FailureCode", a."ChangesJson"::text,
            NULL::text AS "BeforeJson", NULL::text AS "AfterJson", NULL::text AS "Note"
        FROM admin.operation_logs a
        UNION ALL
        SELECT 'security:' || a."Id"::text, a."OccurredAtUtc", COALESCE(u."Email", a."ActorUserId"::text),
            'settings', '修改管理员密码', COALESCE(u."Email", a."ActorUserId"::text), a."ActorUserId"::text,
            '后台 · 账号安全', NULL::varchar, a."Result", a."FailureCode", '[]', NULL, NULL, NULL
        FROM identity.admin_security_audit a LEFT JOIN identity.users u ON u."Id" = a."ActorUserId"
        WHERE a."Operation" = 'admin.password.change'
        UNION ALL
        SELECT 'adjustment:' || a."OperationId"::text, a."AppliedAtUtc", COALESCE(actor."Email", a."ActorUserId"::text),
            'users', CASE WHEN a."Action" = 'grant' THEN '发放用户会员'
                WHEN a."Scope" = 'all' THEN '撤销全部会员权益' ELSE '撤销管理员赠送会员' END,
            COALESCE(target."Email", a."UserId"::text), a."UserId"::text, '后台 · 用户管理', NULL, 'success', NULL,
            '[]', a."BeforeJson"::text, a."AfterJson"::text,
            CASE a."ReasonCode" WHEN 'compensation' THEN '用户补偿' WHEN 'promotion' THEN '活动赠送' WHEN 'trial' THEN '免费体验'
                WHEN 'internal-test' THEN '内部测试' WHEN 'issued-in-error' THEN '发放错误' WHEN 'ineligible' THEN '赠送资格不符'
                WHEN 'test-ended' THEN '内部测试结束' WHEN 'refund-request' THEN '用户申请退款' ELSE '其他原因' END || COALESCE('：' || a."Note", '')
        FROM membership.admin_adjustments a LEFT JOIN identity.users actor ON actor."Id" = a."ActorUserId"
            LEFT JOIN identity.users target ON target."Id" = a."UserId"
        WHERE a."State" = 'applied' AND a."AppliedAtUtc" IS NOT NULL
        UNION ALL
        SELECT 'membership:' || a."OperationId"::text, a."CreatedAtUtc", COALESCE(actor."Email", a."Actor"),
            'users', CASE WHEN a."Action" = 'grant' THEN '人工发放会员' ELSE '人工撤销会员' END,
            COALESCE(target."Email", a."UserId"::text), a."UserId"::text, '管理工具 · 会员管理', NULL, 'success', NULL,
            '[]', NULL, NULL, a."Reason"
        FROM membership.audit a LEFT JOIN identity.users target ON target."Id" = a."UserId"
            LEFT JOIN identity.users actor ON a."Actor" = 'admin:' || actor."Id"::text OR a."Actor" = actor."Id"::text
        WHERE a."Actor" <> 'membership-order' AND a."Actor" NOT LIKE 'admin-adjustment:%' AND a."Actor" NOT LIKE 'admin-refund:%'
        UNION ALL
        SELECT 'order:' || e."Id"::text, e."OccurredAtUtc", COALESCE(actor."Email", e."Actor"),
            'orders', CASE WHEN e."Kind" = 'membership_activated' THEN '重试发放订单权益' ELSE '重试发放订单权益失败' END,
            'FT' || upper(replace(o."Id"::text, '-', '')), o."Id"::text, '后台 · 订单管理', NULL,
            CASE WHEN e."Kind" = 'membership_activated' THEN 'success' ELSE 'failure' END,
            CASE WHEN e."Kind" = 'membership_activation_failed' THEN 'membership_activation_failed' ELSE NULL END,
            '[]', NULL, NULL, target."Email"
        FROM membership.order_events e JOIN membership.orders o ON o."Id" = e."OrderId"
            LEFT JOIN identity.users actor ON e."Actor" = 'admin:' || actor."Id"::text
            LEFT JOIN identity.users target ON target."Id" = o."UserId"
        WHERE e."Actor" LIKE 'admin:%' AND e."Kind" IN ('membership_activated', 'membership_activation_failed')
        """);

    [HttpGet]
    public async Task<AdminAuditPage> List([FromQuery] AdminAuditQuery filters, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime);
        var start = filters.Start ?? today.AddDays(-6);
        var end = filters.End ?? today;
        if (start.Year < 2 || start > end || end == DateOnly.MaxValue) throw new AuthFault(400, "invalid_audit_dates", "Invalid audit date range.");
        var from = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(8)).ToUniversalTime();
        var until = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(8)).ToUniversalTime();
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var query = Records().Where(x => x.OccurredAtUtc >= from && x.OccurredAtUtc < until);
        if (filters.Category != "all") query = query.Where(x => x.Category == filters.Category);
        if (filters.Status != "all") query = query.Where(x => x.Status == filters.Status);
        if (!string.IsNullOrWhiteSpace(filters.Query))
        {
            var value = filters.Query.Trim();
            var pattern = "%" + value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(x => EF.Functions.ILike(x.Administrator, pattern, "\\") || EF.Functions.ILike(x.Target, pattern, "\\") ||
                EF.Functions.ILike(x.Action, pattern, "\\") || EF.Functions.ILike(x.TargetId, pattern, "\\") || EF.Functions.ILike(x.Note!, pattern, "\\"));
        }
        var total = await query.CountAsync(ct);
        var page = Math.Min(filters.Page, Math.Max(1, (int)Math.Ceiling((double)total / filters.PageSize)));
        var rows = await query.OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id)
            .Skip((page - 1) * filters.PageSize).Take(filters.PageSize).ToListAsync(ct);
        await snapshot.CommitAsync(ct);
        return new(rows.Select(Map).ToArray(), total, page, filters.PageSize, now);
    }

    [HttpGet("{id}")]
    public async Task<AdminAuditLog> Detail(string id, CancellationToken ct)
    {
        if (id.Length > 80) throw new AuthFault(404, "audit_not_found", "Audit record not found.");
        var row = await Records().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AuthFault(404, "audit_not_found", "Audit record not found.");
        return Map(row);
    }

    private static AdminAuditLog Map(AdminAuditRow row)
    {
        var changes = JsonSerializer.Deserialize<AdminAuditChange[]>(row.ChangesJson, Json) ?? [];
        if (row.BeforeJson is not null && row.AfterJson is not null)
        {
            var before = JsonSerializer.Deserialize<AdminEffectiveMembership>(row.BeforeJson, Json)!;
            var after = JsonSerializer.Deserialize<AdminEffectiveMembership>(row.AfterJson, Json)!;
            static string Plan(AdminEffectiveMembership value) => !value.IsActive ? "非会员" : value.Plan switch { "monthly" => "月会员", "annual" => "年会员", "lifetime" => "永久会员", _ => "会员" };
            static string? Time(DateTimeOffset? value) => value?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss");
            changes = [new("当前会员", Plan(before), Plan(after)), new("会员有效期至", Time(before.ExpiresAtUtc), Time(after.ExpiresAtUtc)),
                new("会员授权编号", before.GrantId?.ToString(), after.GrantId?.ToString())];
            changes = changes.Where(x => x.Before != x.After).ToArray();
        }
        return new(row.Id, row.OccurredAtUtc, row.Administrator, row.Category, row.Action, row.Target, row.TargetId,
            row.Source, row.Ip, new(row.Status, changes, row.Status == "failure" ? FailureReason(row.FailureCode) : null), row.Note);
    }

    private static string FailureReason(string? code) => code switch {
        "plan_version_conflict" => "套餐已被修改，请刷新后重试。", "invalid_plan_duration" => "套餐时长不符合要求。",
        "paid_or_unknown_entitlements_protected" => "购买或来源未核实的权益受保护，不能直接撤销。",
        "revocation_confirmation_required" => "须二次确认撤销权益不等于退款。",
        "membership_changed" => "用户会员状态已变化，需要重新确认。", "preview_expired" => "确认方案已过期。",
        "preview_not_found" => "未找到会员调整确认方案。", "preview_actor_mismatch" => "确认方案属于其他管理员。",
        "membership_activation_failed" => "会员权益发放失败，支付记录已保留。", "order_not_paid" => "订单尚未支付，不能发放权益。",
        "validation_failed" => "提交的数据未通过校验。", "admin_header_required" or "request_rejected" => "管理操作请求被拒绝。",
        "internal_error" => "服务器执行失败，请检查服务日志。", "current_password_incorrect" => "当前密码验证失败。",
        "password_policy_failed" => "新密码未通过密码策略校验。", "password_unchanged" => "新密码与当前密码相同。",
        "refund_period_expired" => "已超过支付完成后的 72 小时退款期限。", "refund_provider_not_supported" => "支付渠道尚未接入退款，未执行资金退款或权益撤销。",
        "order_already_refunded" => "订单已退款，不能重复退款。", "refund_operation_conflict" => "退款请求标识已用于其他操作。",
        "refund_amount_exceeds_payment" or "invalid_refund_request" => "退款金额或填写内容不符合要求。", "refund_grant_binding_invalid" => "订单权益关联无法核实，操作已停止。",
        "account_locked" => "账号暂时锁定。", _ => code is null ? "操作执行失败。" : $"操作执行失败（{code}）。"
    };
}
