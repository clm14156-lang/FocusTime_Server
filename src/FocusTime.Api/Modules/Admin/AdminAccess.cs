using System.Security.Claims;

namespace FocusTime.Api.Modules.Admin;

public sealed class AdminAccess(IConfiguration configuration)
{
    public const string Policy = "Administrator";
    public const string Path = "/api/v1/admin";
    public const string CookieName = "FocusTime.Admin.Session";
    private readonly HashSet<Guid> userIds =
        (configuration.GetSection("AdminOverview:UserIds").Get<Guid[]>() ?? []).ToHashSet();

    public bool Allows(Guid id) => id != Guid.Empty && userIds.Contains(id);
    public bool Allows(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && Allows(id);
}
