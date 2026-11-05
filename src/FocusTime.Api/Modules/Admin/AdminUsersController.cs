using System.ComponentModel.DataAnnotations;
using System.Data;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.Admin;

public sealed class AdminUserQuery
{
    [MaxLength(256)] public string? Query { get; set; }
    [RegularExpression("^(all|member|nonMember)$")] public string Membership { get; set; } = "all";
    [RegularExpression("^(all|active|disabled|deleted|locked)$")] public string Account { get; set; } = "all";
    [RegularExpression("^(all|today|week|month|quarter)$")] public string Registration { get; set; } = "all";
    [RegularExpression("^(all|members|today|inactive)$")] public string Quick { get; set; } = "all";
    [Range(1, 1000000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 10;
}

public sealed record AdminUser
{
    public Guid Id { get; init; }
    public required string Email { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset RegisteredAtUtc { get; init; }
    public DateTimeOffset? LastLoginAtUtc { get; init; }
    public string? MembershipPlan { get; init; }
    public DateTimeOffset? MembershipStartsAtUtc { get; init; }
    public DateTimeOffset? MembershipExpiresAtUtc { get; init; }
    public DateTimeOffset? LockedUntilUtc { get; init; }
}
public sealed record AdminUserStats(int Total, int Members, int Today, int Inactive);
public sealed record AdminUsersPage(IReadOnlyList<AdminUser> Items, int Total, int Page, int PageSize,
    AdminUserStats Stats, DateTimeOffset ServerTimeUtc, string AccountStatusNote);

[ApiController, Authorize(Policy = AdminAccess.Policy), Route("api/v1/admin/users"),
    ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminUsersController(AppDbContext db, TimeProvider clock) : ControllerBase
{
    // Identity supports finite lockouts and permanent (+infinity) lockouts. There is no soft-delete
    // state in this schema; deleted users are never reconstructed from orders, sessions or audits.
    public const string StatusNote = "已禁用依据 Identity 永久锁定；临时锁定单独显示。当前后端无注销状态字段，不推测或恢复已删除账号。";
    private static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    [HttpGet]
    public async Task<AdminUsersPage> List([FromQuery] AdminUserQuery filters, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = new DateTimeOffset(now.ToOffset(Offset).Date, Offset).ToUniversalTime();
        var tomorrow = today.AddDays(1);
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var all = Rows(now);
        // These totals deliberately precede every list filter and pagination operation.
        var stats = new AdminUserStats(await all.CountAsync(ct),
            await all.CountAsync(x => x.MembershipPlan != null, ct),
            await all.CountAsync(x => x.RegisteredAtUtc >= today && x.RegisteredAtUtc < tomorrow, ct),
            await all.CountAsync(x => x.Status == "disabled" || x.Status == "deleted", ct));
        var matching = all;
        if (!string.IsNullOrWhiteSpace(filters.Query))
        {
            var search = filters.Query.Trim();
            var escaped = search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var pattern = "%" + escaped + "%";
            matching = matching.Where(x => EF.Functions.ILike(x.Email, pattern, "\\") ||
                EF.Functions.ILike(x.Id.ToString(), pattern, "\\"));
        }
        if (filters.Membership == "member") matching = matching.Where(x => x.MembershipPlan != null);
        if (filters.Membership == "nonMember") matching = matching.Where(x => x.MembershipPlan == null);
        if (filters.Account != "all") matching = matching.Where(x => x.Status == filters.Account);
        var days = filters.Registration switch { "today" => 1, "week" => 7, "month" => 30, "quarter" => 90, _ => 0 };
        if (days > 0)
        {
            var start = today.AddDays(1 - days);
            matching = matching.Where(x => x.RegisteredAtUtc >= start && x.RegisteredAtUtc < tomorrow);
        }
        if (filters.Quick == "members") matching = matching.Where(x => x.MembershipPlan != null);
        if (filters.Quick == "today") matching = matching.Where(x => x.RegisteredAtUtc >= today && x.RegisteredAtUtc < tomorrow);
        if (filters.Quick == "inactive") matching = matching.Where(x => x.Status == "disabled" || x.Status == "deleted");
        var total = await matching.CountAsync(ct);
        var page = Math.Min(filters.Page, Math.Max(1, (int)Math.Ceiling((double)total / filters.PageSize)));
        var items = await matching.OrderByDescending(x => x.RegisteredAtUtc).ThenByDescending(x => x.Id)
            .Skip((page - 1) * filters.PageSize).Take(filters.PageSize).ToListAsync(ct);
        await snapshot.CommitAsync(ct);
        return new(items, total, page, filters.PageSize, stats, now, StatusNote);
    }

    [HttpGet("{id:guid}")]
    public async Task<AdminUser> Detail(Guid id, CancellationToken ct) =>
        await Rows(clock.GetUtcNow()).SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new AuthFault(404, "user_not_found", "User not found.");

    private IQueryable<AdminUser> Rows(DateTimeOffset now) =>
        from user in db.Users.AsNoTracking()
        from grant in db.MembershipGrants.AsNoTracking().Where(g => g.UserId == user.Id && g.RevokedAtUtc == null &&
                g.StartsAtUtc <= now && (g.ExpiresAtUtc == null || g.ExpiresAtUtc > now))
            .OrderByDescending(g => g.Kind == "lifetime").ThenByDescending(g => g.ExpiresAtUtc).ThenBy(g => g.Id)
            .Take(1).DefaultIfEmpty()
        select new AdminUser {
            Id = user.Id, Email = user.Email!, Status = user.LockoutEnabled && user.LockoutEnd > now
                ? user.LockoutEnd == DateTimeOffset.MaxValue ? "disabled" : "locked" : "active",
            RegisteredAtUtc = user.CreatedAtUtc,
            LastLoginAtUtc = db.LoginSessions.Where(s => s.UserId == user.Id).Max(s => (DateTimeOffset?)s.CreatedAtUtc),
            MembershipPlan = grant == null ? null : grant.Kind, MembershipStartsAtUtc = grant == null ? null : grant.StartsAtUtc,
            MembershipExpiresAtUtc = grant == null ? null : grant.ExpiresAtUtc,
            LockedUntilUtc = user.LockoutEnabled && user.LockoutEnd > now && user.LockoutEnd != DateTimeOffset.MaxValue ? user.LockoutEnd : null };
}
