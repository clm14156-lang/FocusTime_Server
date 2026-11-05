using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Membership;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Admin;

public sealed class AdminOrderQuery
{
    [MaxLength(256)] public string? Query { get; set; }
    [RegularExpression("^(all|pending|paid|closed)$")] public string Status { get; set; } = "all";
    [RegularExpression("^(all|monthly|annual|lifetime)$")] public string Plan { get; set; } = "all";
    [RegularExpression("^(all|wechat|alipay)$")] public string Method { get; set; } = "all";
    [RegularExpression("^(all|week|month|quarter)$")] public string Range { get; set; } = "all";
    public bool ExceptionOnly { get; set; }
    public Guid? UserId { get; set; }
    [Range(1, 1000000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 10;
}

public sealed record AdminOrderEvent(Guid Id, string Kind, string Actor, DateTimeOffset OccurredAtUtc);
public sealed record AdminOrder(MembershipOrderResponse Order, Guid UserId, string Email,
    DateTimeOffset? MembershipStartsAtUtc, DateTimeOffset? MembershipExpiresAtUtc,
    DateTimeOffset? ActivationAttemptedAtUtc, IReadOnlyList<AdminOrderEvent> Events);
public sealed record AdminOrderStat(string Key, int Count, double Share, int[] Trend);
internal sealed record AdminOrderDay(DateOnly Day, string Status, string ActivationStatus, int Count);
public sealed record AdminOrdersPage(IReadOnlyList<AdminOrder> Items, int Total, int Page, int PageSize,
    IReadOnlyList<AdminOrderStat> Stats, DateTimeOffset ServerTimeUtc);

[ApiController, Authorize(Policy = AdminAccess.Policy), Route("api/v1/admin/orders"),
    ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminOrdersController(AppDbContext db, MembershipOrderService orders, TimeProvider clock) : ControllerBase
{
    private sealed class Row
    {
        public required MembershipOrder Order { get; init; }
        public required string Email { get; init; }
    }

    [HttpGet]
    public async Task<AdminOrdersPage> List([FromQuery] AdminOrderQuery filters, CancellationToken ct)
    {
        // The parameter name must differ from the 'query' search field, or MVC selects a nested prefix.
        var query = filters;
        var now = clock.GetUtcNow();
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var range = InRange(query, now);
        var matching = Filter(range, query, now);
        var total = await matching.CountAsync(ct);
        var page = Math.Min(query.Page, Math.Max(1, (int)Math.Ceiling((double)total / query.PageSize)));
        var rows = await matching.OrderByDescending(x => x.Order.CreatedAtUtc).ThenByDescending(x => x.Order.Id)
            .Skip((page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(ct);
        var items = await MapAsync(rows, now, false, ct);
        // Group in PostgreSQL; charts and totals are derived only from persisted order records.
        var start = RangeStart(query, now);
        var groups = await db.Database.SqlQuery<AdminOrderDay>($"""
            SELECT timezone('Asia/Shanghai', "CreatedAtUtc")::date AS "Day", "Status", "ActivationStatus", count(*)::int AS "Count"
            FROM membership.orders WHERE (CAST({start} AS timestamptz) IS NULL OR "CreatedAtUtc" >= {start})
                AND (CAST({query.UserId} AS uuid) IS NULL OR "UserId" = {query.UserId}) GROUP BY 1, 2, 3
            """).ToListAsync(ct);
        var today = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime);
        var first = groups.Count == 0 ? today : groups.Min(x => x.Day);
        var span = Math.Max(1, today.DayNumber - first.DayNumber + 1);
        var counts = new int[3];
        var trends = new[] { new int[7], new int[7], new int[7] };
        foreach (var group in groups)
        {
            var bucket = Math.Clamp((group.Day.DayNumber - first.DayNumber) * 7 / span, 0, 6);
            counts[0] += group.Count; trends[0][bucket] += group.Count;
            if (group.Status == "paid")
            {
                counts[1] += group.Count; trends[1][bucket] += group.Count;
                if (group.ActivationStatus != "activated") { counts[2] += group.Count; trends[2][bucket] += group.Count; }
            }
        }
        var keys = new[] { "total", "paid", "exceptions" };
        var stats = Enumerable.Range(0, 3).Select(index => new AdminOrderStat(keys[index], counts[index],
            counts[0] == 0 ? 0 : (double)counts[index] / counts[0], trends[index])).ToArray();
        await snapshot.CommitAsync(ct);
        return new(items, total, page, query.PageSize, stats, now);
    }

    [HttpGet("{id:guid}")]
    public Task<AdminOrder> Detail(Guid id, CancellationToken ct) => DetailAsync(id, ct);

    [HttpGet("export")]
    public async Task<IReadOnlyList<AdminOrder>> Export([FromQuery] AdminOrderQuery filters, CancellationToken ct)
    {
        var query = filters;
        var now = clock.GetUtcNow();
        var rows = await Filter(InRange(query, now), query, now).OrderByDescending(x => x.Order.CreatedAtUtc)
            .ThenByDescending(x => x.Order.Id).Take(10001).ToListAsync(ct);
        if (rows.Count > 10000) throw new AuthFault(409, "order_export_too_large", "Narrow the filters to at most 10000 orders.");
        return await MapAsync(rows, now, false, ct);
    }

    [HttpPost("{id:guid}/retry-activation")]
    public async Task<ActionResult<AdminOrder>> Retry(Guid id, CancellationToken ct)
    {
        if (Request.Headers["X-FocusTime-Admin"] != "1") return StatusCode(403);
        await orders.ActivateAsync(id, "admin:" + User.FindFirstValue(ClaimTypes.NameIdentifier), ct);
        return await DetailAsync(id, ct);
    }

    private IQueryable<MembershipOrder> InRange(AdminOrderQuery query, DateTimeOffset now)
    {
        var result = db.MembershipOrders.AsNoTracking();
        if (query.UserId.HasValue) result = result.Where(x => x.UserId == query.UserId);
        var start = RangeStart(query, now);
        if (start.HasValue) result = result.Where(x => x.CreatedAtUtc >= start);
        return result;
    }

    private static DateTimeOffset? RangeStart(AdminOrderQuery query, DateTimeOffset now)
    {
        var days = query.Range switch { "week" => 7, "month" => 30, "quarter" => 90, _ => 0 };
        return days == 0 ? null : new DateTimeOffset(now.ToOffset(TimeSpan.FromHours(8)).Date.AddDays(1 - days),
            TimeSpan.FromHours(8)).ToUniversalTime();
    }

    private IQueryable<Row> Filter(IQueryable<MembershipOrder> source, AdminOrderQuery query, DateTimeOffset now)
    {
        if (query.Status == "pending") source = source.Where(x => x.Status == "pending" && x.ExpiresAtUtc > now);
        if (query.Status == "closed") source = source.Where(x => x.Status == "pending" && x.ExpiresAtUtc <= now);
        if (query.Status == "paid") source = source.Where(x => x.Status == "paid");
        if (query.Plan != "all") source = source.Where(x => x.Plan == query.Plan);
        if (query.Method != "all") source = source.Where(x => x.Method == query.Method);
        if (query.ExceptionOnly) source = source.Where(x => x.Status == "paid" && x.ActivationStatus != "activated");
        var result = from order in source join user in db.Users.AsNoTracking() on order.UserId equals user.Id
            select new Row { Order = order, Email = user.Email! };
        if (!string.IsNullOrWhiteSpace(query.Query))
        {
            var search = query.Query.Trim();
            var pattern = "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            var number = search.StartsWith("FT", StringComparison.OrdinalIgnoreCase) ? search[2..] : search;
            Guid.TryParse(number, out var id);
            number = number.Replace("-", "").ToLowerInvariant();
            result = result.Where(x => EF.Functions.ILike(x.Email, pattern, "\\") || x.Order.Id == id ||
                x.Order.Id.ToString().Replace("-", "").Contains(number));
        }
        return result;
    }

    private async Task<AdminOrder> DetailAsync(Guid id, CancellationToken ct)
    {
        var row = await (from order in db.MembershipOrders.AsNoTracking()
            join user in db.Users.AsNoTracking() on order.UserId equals user.Id
            where order.Id == id select new Row { Order = order, Email = user.Email! }).SingleOrDefaultAsync(ct)
            ?? throw new AuthFault(404, "order_not_found", "Order not found.");
        return (await MapAsync([row], clock.GetUtcNow(), true, ct))[0];
    }

    private async Task<IReadOnlyList<AdminOrder>> MapAsync(IReadOnlyList<Row> rows, DateTimeOffset now, bool includeEvents, CancellationToken ct)
    {
        var grantIds = rows.Where(x => x.Order.MembershipGrantId.HasValue).Select(x => x.Order.MembershipGrantId!.Value).ToArray();
        var grants = await db.MembershipGrants.AsNoTracking().Where(x => grantIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var ids = rows.Select(x => x.Order.Id).ToArray();
        var events = includeEvents ? await db.MembershipOrderEvents.AsNoTracking().Where(x => ids.Contains(x.OrderId))
            .OrderBy(x => x.OccurredAtUtc).ThenBy(x => x.Id).ToListAsync(ct) : [];
        return rows.Select(row =>
        {
            var grant = grants.GetValueOrDefault(row.Order.MembershipGrantId ?? Guid.Empty);
            return new AdminOrder(MembershipOrderService.ToResponse(row.Order, now), row.Order.UserId, row.Email,
                grant?.StartsAtUtc, grant?.ExpiresAtUtc, row.Order.ActivationAttemptedAtUtc,
                events.Where(x => x.OrderId == row.Order.Id).Select(x => new AdminOrderEvent(x.Id, x.Kind, x.Actor, x.OccurredAtUtc)).ToArray());
        }).ToArray();
    }
}
