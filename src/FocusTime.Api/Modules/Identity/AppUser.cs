using Microsoft.AspNetCore.Identity;

namespace FocusTime.Api.Modules.Identity;

public sealed class AppUser : IdentityUser<Guid>
{
    public AppUser() => Id = Guid.NewGuid();

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
