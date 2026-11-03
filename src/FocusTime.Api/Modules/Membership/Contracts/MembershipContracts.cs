namespace FocusTime.Api.Modules.Membership.Contracts;

public enum MembershipKind { Normal, Annual, Lifetime }
public sealed record MembershipResponse(MembershipKind Kind, bool IsActive, DateTimeOffset? StartsAtUtc,
    DateTimeOffset? ExpiresAtUtc, long Version, IReadOnlyList<string> Entitlements, DateTimeOffset ServerTimeUtc);
public sealed record EntitlementLeaseResponse(string Token, DateTimeOffset ExpiresAtUtc, string KeyId);

public static class EntitlementCodes
{
    public const string ForcedMode = "forced_mode";
    public const string AutomaticBlocking = "automatic_blocking";
}
