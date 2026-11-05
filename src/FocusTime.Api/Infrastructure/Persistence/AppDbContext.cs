using FocusTime.Api.Modules.Identity;
using FocusTime.Api.Modules.Admin;
using FocusTime.Api.Modules.FocusSync;
using FocusTime.Api.Modules.Membership;
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
    public DbSet<MembershipGrant> MembershipGrants => Set<MembershipGrant>();
    public DbSet<MembershipState> MembershipStates => Set<MembershipState>();
    public DbSet<MembershipAudit> MembershipAudits => Set<MembershipAudit>();
    public DbSet<MembershipOrder> MembershipOrders => Set<MembershipOrder>();
    public DbSet<MembershipOrderEvent> MembershipOrderEvents => Set<MembershipOrderEvent>();
    public DbSet<MembershipPlan> MembershipPlans => Set<MembershipPlan>();

    public DbSet<AdminSecurityAudit> AdminSecurityAudits => Set<AdminSecurityAudit>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<MembershipPlan>(entity =>
        {
            entity.ToTable("plans", "membership", table => table.HasCheckConstraint("ck_plan_terms",
                "\"Id\" IN ('monthly', 'annual', 'lifetime') AND \"AmountFen\" BETWEEN 1 AND 100000000 AND \"Version\" > 0 AND ((\"Id\" = 'lifetime' AND \"DurationDays\" IS NULL) OR (\"Id\" <> 'lifetime' AND \"DurationDays\" IS NOT NULL AND \"DurationDays\" BETWEEN 1 AND 36500))"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(16);
            entity.Property(x => x.Name).HasMaxLength(64);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<AdminSecurityAudit>(entity =>
        {
            entity.ToTable("admin_security_audit", "identity");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Operation).HasMaxLength(64);
            entity.Property(x => x.Source).HasMaxLength(32);
            entity.Property(x => x.Result).HasMaxLength(16);
            entity.Property(x => x.FailureCode).HasMaxLength(64);
            entity.HasIndex(x => new { x.ActorUserId, x.OccurredAtUtc });
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<MembershipGrant>(entity =>
        {
            entity.ToTable("grants", "membership", table => table.HasCheckConstraint("ck_grant_kind_dates",
                "(\"Kind\" = 'lifetime' AND \"ExpiresAtUtc\" IS NULL) OR (\"Kind\" IN ('monthly', 'annual') AND \"ExpiresAtUtc\" IS NOT NULL AND \"ExpiresAtUtc\" > \"StartsAtUtc\")"));
            entity.HasKey(x => x.Id); entity.Property(x => x.Kind).HasMaxLength(16);
            entity.HasIndex(x => new { x.UserId, x.RevokedAtUtc, x.ExpiresAtUtc });
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MembershipState>(entity =>
        {
            entity.ToTable("states", "membership"); entity.HasKey(x => x.UserId);
            entity.HasOne<AppUser>().WithOne().HasForeignKey<MembershipState>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MembershipOrder>(entity =>
        {
            entity.ToTable("orders", "membership", table =>
            {
                table.HasCheckConstraint("ck_order_terms", "\"Plan\" IN ('monthly', 'annual', 'lifetime') AND \"AmountFen\" > 0 AND \"Method\" IN ('wechat', 'alipay') AND \"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                table.HasCheckConstraint("ck_order_payment", "(\"Status\" = 'pending' AND \"PaidAtUtc\" IS NULL) OR (\"Status\" = 'paid' AND \"PaidAtUtc\" IS NOT NULL AND \"PaidAtUtc\" >= \"CreatedAtUtc\" AND \"PaidAtUtc\" < \"ExpiresAtUtc\")");
                table.HasCheckConstraint("ck_order_activation", "(\"ActivationStatus\" IN ('notStarted', 'failed') AND \"MembershipGrantId\" IS NULL AND \"ActivatedAtUtc\" IS NULL) OR (\"Status\" = 'paid' AND \"ActivationStatus\" = 'activated' AND \"MembershipGrantId\" IS NOT NULL AND \"ActivatedAtUtc\" IS NOT NULL)");
                table.HasCheckConstraint("ck_order_duration_snapshot", "(\"Plan\" = 'lifetime' AND \"DurationDays\" IS NULL AND \"DurationMonths\" IS NULL) OR (\"Plan\" <> 'lifetime' AND ((\"DurationDays\" > 0 AND \"DurationDays\" IS NOT NULL AND \"DurationMonths\" IS NULL) OR (\"DurationMonths\" > 0 AND \"DurationMonths\" IS NOT NULL AND \"DurationDays\" IS NULL)))");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Plan).HasMaxLength(16);
            entity.Property(x => x.PlanName).HasMaxLength(64);
            entity.HasOne<MembershipPlan>().WithMany().HasForeignKey(x => x.Plan).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.Method).HasMaxLength(16);
            entity.Property(x => x.Status).HasMaxLength(16);
            entity.Property(x => x.ActivationStatus).HasMaxLength(16);
            entity.Property(x => x.ActivationErrorCode).HasMaxLength(64);
            entity.Property(x => x.PaymentProvider).HasMaxLength(32);
            entity.Property(x => x.PaymentReference).HasMaxLength(128);
            entity.HasIndex(x => new { x.UserId, x.RequestId }).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.Status, x.ActivationStatus, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.PaymentProvider, x.PaymentReference }).IsUnique();
            entity.HasOne<MembershipGrant>().WithMany().HasForeignKey(x => x.MembershipGrantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MembershipOrderEvent>(entity =>
        {
            entity.ToTable("order_events", "membership");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasMaxLength(64);
            entity.Property(x => x.Actor).HasMaxLength(128);
            entity.HasIndex(x => new { x.OrderId, x.OccurredAtUtc });
            entity.HasOne<MembershipOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MembershipAudit>(entity =>
        {
            entity.ToTable("audit", "membership"); entity.HasKey(x => new { x.UserId, x.OperationId });
            entity.Property(x => x.Action).HasMaxLength(16); entity.Property(x => x.RequestHash).HasMaxLength(64);
            entity.Property(x => x.Reason).HasMaxLength(512); entity.Property(x => x.Actor).HasMaxLength(128);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
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
