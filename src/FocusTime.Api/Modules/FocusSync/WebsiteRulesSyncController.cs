using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using FocusTime.Api.Infrastructure.Persistence;
using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Membership;
using FocusTime.Api.Modules.Sessions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.FocusSync;

[ApiController, Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/sync/website-rules")]
public sealed class WebsiteRulesSyncController(AppDbContext db, TimeProvider clock, MembershipService membership,
    SyncNotifications notifications,
    ILogger<WebsiteRulesSyncController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public Task<WebsiteRuleSnapshot> Get(CancellationToken token) => ReadSnapshotAsync(token);

    [HttpGet("quota")]
    public async Task<WebsiteRuleQuota> Quota(CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AccountTransactions.LockSessionAsync(db, clock, User, Request.Headers.Authorization.ToString(), token);
        var current = await ReadSnapshotAsync(token);
        var quota = await ReadQuotaAsync(current.Rules.Count, token);
        await transaction.CommitAsync(token);
        return quota;
    }

    private async Task<WebsiteRuleQuota> ReadQuotaAsync(int count, CancellationToken token)
    {
        var status = await membership.GetAsync(UserId, token);
        return new(count, status.IsActive ? 100 : 10, status.IsActive);
    }

    private async Task<WebsiteRuleSnapshot> ReadSnapshotAsync(CancellationToken token)
    {
        var stored = await db.Database.SqlQuery<StoredWebsiteRules>($"""
            SELECT revision AS "Revision", rules::text AS "Rules"
            FROM focus.website_rule_sets WHERE user_id={UserId}
            """).SingleOrDefaultAsync(token);
        return stored is null ? new(0, []) : new(stored.Revision,
            JsonSerializer.Deserialize<WebsiteRule[]>(stored.Rules, Json)!);
    }

    [HttpPut]
    public async Task<ActionResult<WebsiteRuleSnapshot>> Put(UpdateWebsiteRules request, CancellationToken token)
    {
        if (request.ExpectedRevision < 0 || request.Rules is null ||
            request.Rules.Any(rule => rule is null || !IsValid(rule)) ||
            request.Rules.Select(rule => rule.Id).Distinct().Count() != request.Rules.Count)
            return BadRequest(new { code = "validation_failed" });

        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var session = await AccountTransactions.LockSessionAsync(db, clock, User, Request.Headers.Authorization.ToString(), token);
        var current = await ReadSnapshotAsync(token);
        if (current.Revision != request.ExpectedRevision) return Conflict(new { code = "website_rules_changed" });
        var revision = current.Revision;
        var original = current.Rules.ToDictionary(rule => rule.Id);
        var rules = request.Rules.ToDictionary(rule => rule.Id);
        if (rules.Keys.Except(original.Keys).Any())
        {
            var quota = await ReadQuotaAsync(original.Count, token);
            if (rules.Count > quota.Limit)
                return Conflict(new { code = "website_limit_reached", quota });
        }
        if (rules.Values.Any(rule => AddressChanged(original.GetValueOrDefault(rule.Id), rule) &&
                                     HasDuplicateAddress(rules.Values, rule)))
            return Conflict(new { code = "duplicate_website" });

        foreach (var id in original.Keys.Union(rules.Keys))
            if (original.GetValueOrDefault(id) != rules.GetValueOrDefault(id))
                await SaveChangeAsync(id, ++revision, rules.GetValueOrDefault(id), token);
        var result = Snapshot(revision, rules.Values);
        if (revision != current.Revision) await SaveSnapshotAsync(result, token);
        await transaction.CommitAsync(token);
        if (revision != current.Revision) notifications.Publish(UserId, session.Id, SyncChangeSections.Websites);
        return result;
    }

    [HttpGet("changes")]
    public async Task<WebsiteDeltaPage> Changes(long cursor = 0, CancellationToken token = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AccountTransactions.LockSessionAsync(db, clock, User, Request.Headers.Authorization.ToString(), token);
        var revision = await db.Database.SqlQuery<long>($"""
            SELECT revision AS "Value" FROM focus.website_rule_sets WHERE user_id={UserId}
            """).SingleOrDefaultAsync(token);
        if (cursor < 0 || cursor > revision) throw FocusCursorCodec.Invalid();
        var rows = await db.Database.SqlQuery<StoredWebsiteChange>($"""
            SELECT rule_id AS "Id", revision AS "Revision", rule::text AS "Rule"
            FROM focus.website_rule_changes WHERE user_id={UserId} AND revision>{cursor}
            ORDER BY revision LIMIT 51
            """).ToListAsync(token);
        var more = rows.Count > 50;
        var changes = rows.Take(50).Select(row => new WebsiteChange(row.Id, row.Revision,
            row.Rule is null ? null : JsonSerializer.Deserialize<WebsiteRule>(row.Rule, Json))).ToArray();
        await transaction.CommitAsync(token);
        logger.LogDebug("Website delta pull returned {Changes} changes (cursor: {Cursor}, more: {More}).", changes.Length, cursor, more);
        return new(more ? changes[^1].Revision : revision, more, changes);
    }

    [HttpPost("push")]
    public async Task<WebsiteDeltaAck> Push(WebsiteDeltaPush request, CancellationToken token)
    {
        if (request.Edits is not { Count: >= 1 and <= 50 } ||
            request.Edits.Any(edit => edit is null || edit.Id == Guid.Empty || edit.LocalVersion < 1 || edit.BaseRevision < 0 ||
                edit.Rule is { } rule && (rule.Id != edit.Id || !IsValid(rule))) ||
            request.Edits.Select(edit => edit.Id).Distinct().Count() != request.Edits.Count)
            throw new AuthFault(400, "validation_failed", "Invalid website rule edits.");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var session = await AccountTransactions.LockSessionAsync(db, clock, User, Request.Headers.Authorization.ToString(), token);
        var current = await ReadSnapshotAsync(token);
        var revision = current.Revision;
        var rules = current.Rules.ToDictionary(rule => rule.Id);
        var ids = request.Edits.Select(edit => edit.Id).ToArray();
        var versions = await db.Database.SqlQuery<StoredWebsiteChange>($"""
            SELECT rule_id AS "Id", revision AS "Revision", NULL::text AS "Rule"
            FROM focus.website_rule_changes WHERE user_id={UserId} AND rule_id=ANY({ids})
            """).ToDictionaryAsync(row => row.Id, row => row.Revision, token);
        var receipts = new Dictionary<Guid, WebsiteReceipt>(request.Edits.Count);
        WebsiteRuleQuota? quota = null;
        var quotaRejected = false;
        // Apply deletions before additions so the batch can use any slots it actually frees.
        foreach (var edit in request.Edits.OrderBy(edit => edit.Rule is null ? 0 : rules.ContainsKey(edit.Id) ? 1 : 2))
        {
            var known = versions.GetValueOrDefault(edit.Id);
            var existing = rules.GetValueOrDefault(edit.Id);
            if (existing == edit.Rule && (known > 0 || edit.Rule is not null))
            {
                receipts.Add(edit.Id, new(edit.Id, edit.LocalVersion, known, true));
                continue;
            }
            if (edit.BaseRevision != known)
            {
                receipts.Add(edit.Id, new(edit.Id, edit.LocalVersion, known, false));
                continue;
            }
            if (edit.Rule is { } rule)
            {
                if (existing is null)
                {
                    quota ??= await ReadQuotaAsync(rules.Count, token);
                    if (rules.Count >= quota.Limit)
                    {
                        quotaRejected = true;
                        receipts.Add(edit.Id, new(edit.Id, edit.LocalVersion, known, false, "website_limit_reached"));
                        continue;
                    }
                }
                if (AddressChanged(existing, rule) && HasDuplicateAddress(rules.Values, rule))
                {
                    receipts.Add(edit.Id, new(edit.Id, edit.LocalVersion, known, false, "duplicate_website"));
                    continue;
                }
                rules[edit.Id] = rule;
            }
            else rules.Remove(edit.Id);
            await SaveChangeAsync(edit.Id, ++revision, edit.Rule, token);
            receipts.Add(edit.Id, new(edit.Id, edit.LocalVersion, revision, true));
        }
        if (revision != current.Revision) await SaveSnapshotAsync(Snapshot(revision, rules.Values), token);
        await transaction.CommitAsync(token);
        if (revision != current.Revision) notifications.Publish(UserId, session.Id, SyncChangeSections.Websites);
        logger.LogDebug("Website delta push received {Received}, committed {Committed}, conflicts {Conflicts}.",
            request.Edits.Count, revision - current.Revision, receipts.Values.Count(receipt => !receipt.Accepted));
        return new(request.Edits.Select(edit => receipts[edit.Id]).ToArray(),
            quotaRejected ? quota! with { Count = rules.Count } : null);
    }

    private Task SaveChangeAsync(Guid id, long revision, WebsiteRule? rule, CancellationToken token)
    {
        var json = rule is null ? null : JsonSerializer.Serialize(rule, Json);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO focus.website_rule_changes(user_id,rule_id,revision,rule)
            VALUES({UserId},{id},{revision},CAST({json} AS jsonb))
            ON CONFLICT(user_id,rule_id) DO UPDATE SET revision=excluded.revision,rule=excluded.rule
            """, token);
    }

    private Task SaveSnapshotAsync(WebsiteRuleSnapshot snapshot, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(snapshot.Rules, Json);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO focus.website_rule_sets(user_id,revision,rules)
            VALUES({UserId},{snapshot.Revision},CAST({json} AS jsonb))
            ON CONFLICT(user_id) DO UPDATE SET revision=excluded.revision,rules=excluded.rules
            """, token);
    }

    private static WebsiteRuleSnapshot Snapshot(long revision, IEnumerable<WebsiteRule> rules)
        => new(revision, rules.OrderBy(rule => rule.SortOrder).ThenBy(rule => rule.Id).ToArray());

    private static bool AddressChanged(WebsiteRule? existing, WebsiteRule rule)
        => existing is null || !SameAddress(existing.Address, rule.Address);

    private static bool HasDuplicateAddress(IEnumerable<WebsiteRule> rules, WebsiteRule candidate)
        => rules.Any(rule => rule.Id != candidate.Id && SameAddress(rule.Address, candidate.Address));

    private static bool SameAddress(string first, string second)
        => string.Equals(WithoutWww(first), WithoutWww(second), StringComparison.OrdinalIgnoreCase);

    private static string WithoutWww(string address)
        => address.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? address[4..] : address;

    private static bool IsValid(WebsiteRule rule) => rule.Id != Guid.Empty &&
        !string.IsNullOrWhiteSpace(rule.Name) && rule.Name.Length <= 4096 && !rule.Name.Contains('\0') &&
        rule.SortOrder >= 0 && IsDomain(rule.Address);

    private static bool IsDomain(string? domain)
    {
        if (domain is not { Length: > 0 and <= 253 } || Uri.CheckHostName(domain) != UriHostNameType.Dns)
            return false;
        var labels = domain.Split('.');
        return labels.Length >= 2 && labels.All(label =>
            label.Length is >= 1 and <= 63 && label[0] != '-' && label[^1] != '-' &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    public sealed record StoredWebsiteRules(long Revision, string Rules);
    public sealed record WebsiteRuleSnapshot(long Revision, IReadOnlyList<WebsiteRule> Rules);
    public sealed record WebsiteRuleQuota(int Count, int Limit, bool IsVip);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record UpdateWebsiteRules(long ExpectedRevision, IReadOnlyList<WebsiteRule> Rules);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record WebsiteRule(Guid Id, string Name, string Address, bool IsEnabled, int SortOrder);
    public sealed record StoredWebsiteChange(Guid Id, long Revision, string? Rule);
    public sealed record WebsiteChange(Guid Id, long Revision, WebsiteRule? Rule);
    public sealed record WebsiteDeltaPage(long NextCursor, bool HasMore, IReadOnlyList<WebsiteChange> Changes);
    public sealed record WebsiteEdit(Guid Id, long LocalVersion, long BaseRevision, WebsiteRule? Rule);
    public sealed record WebsiteDeltaPush(IReadOnlyList<WebsiteEdit> Edits);
    public sealed record WebsiteReceipt(Guid Id, long LocalVersion, long Revision, bool Accepted, string? Code = null);
    public sealed record WebsiteDeltaAck(IReadOnlyList<WebsiteReceipt> Receipts, WebsiteRuleQuota? Quota = null);
}
