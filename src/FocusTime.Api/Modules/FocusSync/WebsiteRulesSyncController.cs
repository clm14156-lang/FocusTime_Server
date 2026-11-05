using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using FocusTime.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.FocusSync;

[ApiController, Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/sync/website-rules")]
public sealed class WebsiteRulesSyncController(AppDbContext db) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<WebsiteRuleSnapshot> Get(CancellationToken token)
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
            request.Rules.Any(rule => rule is null || rule.Id == Guid.Empty || string.IsNullOrWhiteSpace(rule.Name) ||
                rule.SortOrder < 0 || !IsDomain(rule.Address)) ||
            request.Rules.Select(rule => rule.Id).Distinct().Count() != request.Rules.Count)
            return BadRequest(new { code = "validation_failed" });

        var rules = request.Rules.OrderBy(rule => rule.SortOrder).ThenBy(rule => rule.Id).ToArray();
        var json = JsonSerializer.Serialize(rules, Json);
        var userId = UserId;
        var count = request.ExpectedRevision == 0
            ? await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO focus.website_rule_sets(user_id,revision,rules)
                VALUES ({userId},1,CAST({json} AS jsonb)) ON CONFLICT(user_id) DO NOTHING
                """, token)
            : await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE focus.website_rule_sets SET revision=revision+1,rules=CAST({json} AS jsonb)
                WHERE user_id={userId} AND revision={request.ExpectedRevision}
                """, token);
        if (count == 0) return Conflict(new { code = "website_rules_changed" });
        return new WebsiteRuleSnapshot(request.ExpectedRevision + 1, rules);
    }

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
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record UpdateWebsiteRules(long ExpectedRevision, IReadOnlyList<WebsiteRule> Rules);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record WebsiteRule(Guid Id, string Name, string Address, bool IsEnabled, int SortOrder);
}
