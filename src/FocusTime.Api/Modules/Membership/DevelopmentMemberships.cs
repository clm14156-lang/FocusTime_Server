using FocusTime.Api.Modules.Identity.Contracts;
using FocusTime.Api.Modules.Membership.Contracts;

namespace FocusTime.Api.Modules.Membership;

// Local test fixture until persistent membership grants and signed leases are implemented.
// Only the server's development configuration can grant this identity; auth requests cannot.
public sealed class DevelopmentMemberships(IHostEnvironment environment, IConfiguration configuration)
{
    public UserProfile Apply(UserProfile user)
    {
        var kind = environment.IsDevelopment() && user.EmailConfirmed &&
            string.Equals(configuration[$"DevelopmentMemberships:{user.Id:D}"], "lifetime", StringComparison.Ordinal)
            ? MembershipKind.Lifetime : MembershipKind.Normal;
        return user with { Membership = kind.ToString().ToLowerInvariant() };
    }
}
