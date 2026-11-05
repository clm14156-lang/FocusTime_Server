namespace FocusTime.Api.Modules.Membership;

public sealed class MembershipPlan
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int AmountFen { get; set; }
    public int? DurationDays { get; set; }
    public bool Available { get; set; }
    public bool Recommended { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}

public sealed record MembershipPlanResponse(string Id, string Name, int AmountFen, int? DurationDays,
    bool Available, bool Recommended, long Version, DateTimeOffset UpdatedAtUtc, string Currency = "CNY");

public static class MembershipPlanMapping
{
    public static MembershipPlanResponse ToResponse(MembershipPlan plan) => new(plan.Id, plan.Name,
        plan.AmountFen, plan.DurationDays, plan.Available, plan.Recommended, plan.Version, plan.UpdatedAtUtc);
}
