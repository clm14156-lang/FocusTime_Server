using System.Data;
using FocusTime.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Admin;

[ApiController]
[Route("api/v1/admin/overview")]
[Authorize(Policy = AdminAccess.Policy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminOverviewController(AppDbContext db, TimeProvider clock) : ControllerBase
{
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    [HttpGet]
    public async Task<ActionResult<OverviewResponse>> Get([FromQuery] int days = 7, CancellationToken ct = default)
    {
        if (days is not (7 or 30 or 90))
            return BadRequest(new ProblemDetails { Title = "days must be 7, 30 or 90.", Status = 400 });

        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.ToOffset(BeijingOffset).DateTime);
        var firstDay = today.AddDays(1 - days);
        var start = new DateTimeOffset(firstDay.ToDateTime(TimeOnly.MinValue), BeijingOffset).ToUniversalTime();

        // One consistent database snapshot for totals, series and recent records.
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var counts = await db.Database.SqlQuery<OverviewDay>($"""
            SELECT day AS "Date", sum(users)::int AS "Users", sum(members)::int AS "Members"
            FROM (
                SELECT timezone('Asia/Shanghai', "CreatedAtUtc")::date AS day, count(*) AS users, 0 AS members
                FROM identity.users WHERE "CreatedAtUtc" >= {start} AND "CreatedAtUtc" <= {now} GROUP BY 1
                UNION ALL
                SELECT timezone('Asia/Shanghai', first_start)::date, 0, count(*)
                FROM (
                    SELECT min("StartsAtUtc") AS first_start FROM membership.grants
                    WHERE "StartsAtUtc" <= {now} AND ("RevokedAtUtc" IS NULL OR "RevokedAtUtc" > "StartsAtUtc")
                    GROUP BY "UserId"
                ) first_memberships WHERE first_start >= {start} GROUP BY 1
            ) activity GROUP BY day
            """).ToListAsync(ct);

        var byDate = counts.ToDictionary(point => point.Date);
        var trend = Enumerable.Range(0, days).Select(offset =>
        {
            var date = firstDay.AddDays(offset);
            return byDate.GetValueOrDefault(date) ?? new OverviewDay(date, 0, 0);
        }).ToArray();
        var membershipCounts = await db.Database.SqlQuery<OverviewMembershipCount>($"""
            SELECT "Kind", count(*)::int AS "Count" FROM (
                SELECT DISTINCT ON ("UserId") "Kind" FROM membership.grants
                WHERE "RevokedAtUtc" IS NULL AND "StartsAtUtc" <= {now}
                    AND ("ExpiresAtUtc" IS NULL OR "ExpiresAtUtc" > {now})
                ORDER BY "UserId", ("Kind" = 'lifetime') DESC, "ExpiresAtUtc" DESC, "Id"
            ) current_grants GROUP BY "Kind"
            """).ToListAsync(ct);

        var total = await db.Users.CountAsync(ct);
        var active = membershipCounts.Sum(plan => plan.Count);
        var distribution = membershipCounts.Select(plan =>
            new OverviewMembership(plan.Kind, plan.Count, (double)plan.Count / active)).ToList();
        var summary = new OverviewSummary(total, active, trend[^1].Users, trend.TakeLast(7).Sum(day => day.Users),
            total == 0 ? 0 : (double)active / total, trend[^1].Users - trend[^2].Users);
        var users = await db.Users.AsNoTracking().OrderByDescending(user => user.CreatedAtUtc).ThenByDescending(user => user.Id)
            .Take(5).Select(user => new OverviewUser(user.Id, user.Email!, user.CreatedAtUtc)).ToListAsync(ct);
        var orders = await (from order in db.MembershipOrders.AsNoTracking()
            join user in db.Users.AsNoTracking() on order.UserId equals user.Id
            orderby order.CreatedAtUtc descending, order.Id descending
            select new OverviewOrder(order.Id, user.Email!, order.Plan,
                order.Status == "pending" && order.ExpiresAtUtc <= now ? "expired" : order.Status, order.CreatedAtUtc))
            .Take(5).ToListAsync(ct);
        await snapshot.CommitAsync(ct);
        return new OverviewResponse(summary, trend, distribution, orders, users);
    }
}

public sealed record OverviewDay(DateOnly Date, int Users, int Members);
internal sealed record OverviewMembershipCount(string Kind, int Count);
public sealed record OverviewMembership(string Kind, int Count, double Share);
public sealed record OverviewSummary(int TotalUsers, int ActiveMembers, int NewUsersToday, int NewUsersLastSevenDays,
    double MemberShare, int UsersDayChange);
public sealed record OverviewUser(Guid Id, string Email, DateTimeOffset RegisteredAtUtc);
public sealed record OverviewOrder(Guid Id, string Email, string Plan, string Status, DateTimeOffset CreatedAtUtc);
public sealed record OverviewResponse(OverviewSummary Summary, OverviewDay[] Trend, List<OverviewMembership> Membership,
    List<OverviewOrder> RecentOrders, List<OverviewUser> RecentUsers);
