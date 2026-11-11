using FocusTime.Api.Modules.Identity;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Modules.AccountSecurity;

public sealed class SecurityChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid SessionId { get; set; }
    public string Email { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string StampHash { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public Guid? ParentId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
    public bool Consumed { get; set; }
    public string? ProofHash { get; set; }
    public DateTimeOffset? ProofExpiresAtUtc { get; set; }
    public bool ProofConsumed { get; set; }
}

public sealed class AccountSecurityAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid SessionId { get; set; }
    public string Operation { get; set; } = "";
    public string Result { get; set; } = "";
    public string? FailureCode { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class SecurityNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Recipient { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public bool Delivered { get; set; }
}

public static class AccountSecurityModel
{
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<SecurityChallenge>(e =>
        {
            e.ToTable("security_challenges", "identity"); e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(256); e.Property(x => x.Purpose).HasMaxLength(24);
            e.Property(x => x.StampHash).HasMaxLength(64); e.Property(x => x.CodeHash).HasMaxLength(64);
            e.Property(x => x.ProofHash).HasMaxLength(64); e.HasIndex(x => x.ProofHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.Purpose, x.CreatedAtUtc });
            e.HasIndex(x => new { x.Email, x.CreatedAtUtc });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<AccountSecurityAudit>(e =>
        {
            e.ToTable("security_audits", "identity"); e.HasKey(x => x.Id);
            e.Property(x => x.Operation).HasMaxLength(64); e.Property(x => x.Result).HasMaxLength(16);
            e.Property(x => x.FailureCode).HasMaxLength(64);
            e.HasIndex(x => new { x.UserId, x.OccurredAtUtc });
            // No credentials, verification codes or request bodies are written to this log.
        });
        builder.Entity<SecurityNotification>(e =>
        {
            e.ToTable("security_notifications", "identity"); e.HasKey(x => x.Id);
            e.Property(x => x.Recipient).HasMaxLength(256); e.Property(x => x.Subject).HasMaxLength(256);
            e.Property(x => x.Body).HasMaxLength(4000);
            e.HasIndex(x => new { x.CompletedAtUtc, x.NextAttemptAtUtc });
        });
    }
}
