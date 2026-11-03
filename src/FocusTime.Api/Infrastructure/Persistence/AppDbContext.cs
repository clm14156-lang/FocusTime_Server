using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.FocusSync;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FocusTime.Api.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityUserContext<AppUser, Guid>(options)
{
    public DbSet<EmailChallenge> EmailChallenges => Set<EmailChallenge>();
    public DbSet<LoginDevice> LoginDevices => Set<LoginDevice>();
    public DbSet<LoginSession> LoginSessions => Set<LoginSession>();
    public DbSet<RefreshCredential> RefreshCredentials => Set<RefreshCredential>();
    public DbSet<FocusRecord> FocusRecords => Set<FocusRecord>();
    public DbSet<FocusSyncState> FocusSyncStates => Set<FocusSyncState>();
    public DbSet<FocusSyncReceipt> FocusSyncReceipts => Set<FocusSyncReceipt>();
    public DbSet<FocusChange> FocusChanges => Set<FocusChange>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<AppUser>(user =>
        {
            user.ToTable("users", "identity");
            user.Property(value => value.Email).IsRequired();
            user.Property(value => value.NormalizedEmail).IsRequired();
            user.HasIndex(value => value.NormalizedEmail).IsUnique().HasDatabaseName("ux_users_normalized_email");
        });
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims", "identity");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins", "identity");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens", "identity");
        builder.Entity<FocusRecord>(entity =>
        {
            entity.ToTable("records", "focus"); entity.HasKey(x => new { x.UserId, x.RecordId });
            entity.HasIndex(x => new { x.UserId, x.Revision }).IsUnique();
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb"); entity.Property(x => x.PayloadHash).HasMaxLength(64);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<FocusChange>(entity =>
        {
            entity.ToTable("changes", "focus"); entity.HasKey(x => new { x.UserId, x.Revision });
            entity.HasIndex(x => new { x.UserId, x.RecordId, x.Revision });
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<FocusSyncState>(entity =>
        {
            entity.ToTable("sync_states", "focus"); entity.HasKey(x => x.UserId);
            entity.HasOne<AppUser>().WithOne().HasForeignKey<FocusSyncState>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<FocusSyncReceipt>(entity =>
        {
            entity.ToTable("receipts", "focus"); entity.HasKey(x => new { x.UserId, x.OperationId });
            entity.Property(x => x.RequestHash).HasMaxLength(64); entity.Property(x => x.Status).HasMaxLength(24);
            entity.Property(x => x.Code).HasMaxLength(64);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<EmailChallenge>(entity =>
        {
            entity.ToTable("email_challenges", "identity");
            entity.Property(x => x.Email).HasMaxLength(256);
            entity.Property(x => x.Purpose).HasMaxLength(24);
            entity.Property(x => x.CodeHash).HasMaxLength(64);
            entity.Property(x => x.TicketHash).HasMaxLength(64);
            entity.HasIndex(x => new { x.Email, x.Purpose, x.CreatedAtUtc });
            entity.HasIndex(x => x.TicketHash).IsUnique();
        });
        builder.Entity<LoginDevice>(entity =>
        {
            entity.ToTable("devices", "identity");
            entity.HasIndex(x => new { x.UserId, x.InstallationId }).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.Property(x => x.OperatingSystem).HasMaxLength(64);
            entity.Property(x => x.ClientVersion).HasMaxLength(32);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<LoginSession>(entity =>
        {
            entity.ToTable("sessions", "identity");
            entity.HasIndex(x => x.AccessHash).IsUnique();
            entity.Property(x => x.AccessHash).HasMaxLength(64);
            entity.Property(x => x.SecurityStamp).HasMaxLength(64);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<LoginDevice>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<RefreshCredential>(entity =>
        {
            entity.ToTable("refresh_credentials", "identity");
            entity.Property(x => x.TokenHash).HasMaxLength(64);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasOne<LoginSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
