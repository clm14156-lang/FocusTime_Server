namespace FocusTime.Api.Modules.Membership;

public sealed class RegistrationTrialRule
{
    public const int SingletonId = 1;
    public const int MinimumDays = 1;
    public const int MaximumDays = 15;

    public int Id { get; set; }
    public int DurationDays { get; set; }
    public long Version { get; set; }
}
