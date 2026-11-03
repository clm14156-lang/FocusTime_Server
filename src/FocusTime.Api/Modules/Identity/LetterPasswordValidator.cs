using Microsoft.AspNetCore.Identity;

namespace FocusTime.Api.Modules.Identity;

public sealed class LetterPasswordValidator : IPasswordValidator<AppUser>
{
    public Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user, string? password) =>
        Task.FromResult(password is { Length: <= 128 } && password.Any(char.IsLetter)
            ? IdentityResult.Success
            : IdentityResult.Failed(new IdentityError { Code = "PasswordRequiresLetter",
                Description = "Password must contain a letter and at most 128 characters." }));
}
