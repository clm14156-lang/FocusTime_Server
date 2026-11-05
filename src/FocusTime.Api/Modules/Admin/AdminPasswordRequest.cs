using System.ComponentModel.DataAnnotations;

namespace FocusTime.Api.Modules.Admin;

public sealed class AdminPasswordRequest
{
    [Required, MaxLength(128)] public string CurrentPassword { get; init; } = "";
    [Required, MinLength(8), MaxLength(128)] public string NewPassword { get; init; } = "";
    [Required, MaxLength(128), Compare(nameof(NewPassword))] public string ConfirmPassword { get; init; } = "";
}
