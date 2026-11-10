using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LabGuard.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AdminAccount> AdminAccounts => Set<AdminAccount>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<LoginRequest> LoginRequests => Set<LoginRequest>();
    public DbSet<PcRegistrationRequest> PcRegistrationRequests => Set<PcRegistrationRequest>();
    public DbSet<Pc> Pcs => Set<Pc>();
    public DbSet<PcSession> PcSessions => Set<PcSession>();
    public DbSet<ServerSettings> ServerSettingsRows => Set<ServerSettings>();
    public DbSet<ScreenViewEvent> ScreenViewEvents => Set<ScreenViewEvent>();
    public DbSet<ScreenControlEvent> ScreenControlEvents => Set<ScreenControlEvent>();
    public DbSet<PcMessage> PcMessages => Set<PcMessage>();
    public DbSet<ScreenBackground> ScreenBackgrounds => Set<ScreenBackground>();
    public DbSet<ClientRelease> ClientReleases => Set<ClientRelease>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite has no native date type; unix milliseconds compare reliably as integers.
        var unixMilliseconds = new ValueConverter<DateTimeOffset, long>(
            value => value.ToUnixTimeMilliseconds(),
            value => DateTimeOffset.FromUnixTimeMilliseconds(value));

        modelBuilder.Entity<AdminAccount>(entity =>
        {
            entity.Property(a => a.Username).HasMaxLength(64).UseCollation("NOCASE").IsRequired();
            entity.HasIndex(a => a.Username).IsUnique();
            entity.Property(a => a.PasswordHash).IsRequired();
            entity.Property(a => a.LockedUntilUtc).HasConversion(unixMilliseconds);
        });

        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.Property(u => u.Username).HasMaxLength(64).UseCollation("NOCASE").IsRequired();
            entity.HasIndex(u => u.Username).IsUnique();
            entity.Property(u => u.PasswordHash).IsRequired();
            entity.Property(u => u.LastLoginUtc).HasConversion(unixMilliseconds);
        });

        modelBuilder.Entity<LoginRequest>(entity =>
        {
            entity.Property(r => r.PcName).HasMaxLength(64).IsRequired();
            entity.Property(r => r.RequestedAtUtc).HasConversion(unixMilliseconds);
            entity.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(r => r.DecidedAtUtc).HasConversion(unixMilliseconds);
            entity.Property(r => r.Reason).HasMaxLength(32);
            entity.HasIndex(r => r.Status);
            // The latest request of a PC, for the request cooldown and the heartbeat.
            entity.HasIndex(r => new { r.PcId, r.RequestedAtUtc });
            // At most one pending request per PC, enforced by the database. Null PCs (the samples) never collide.
            entity.HasIndex(r => r.PcId)
                .IsUnique()
                .HasFilter("\"Status\" = 'Pending'")
                .HasDatabaseName("IX_LoginRequests_PcId_Pending");
        });

        modelBuilder.Entity<PcRegistrationRequest>(entity =>
        {
            entity.Property(r => r.Name).HasMaxLength(32).IsRequired();
            entity.Property(r => r.IpAddress).HasMaxLength(45);
            entity.Property(r => r.Commitment).HasMaxLength(64).IsRequired();
            entity.Property(r => r.ServerNonce).HasMaxLength(64).IsRequired();
            entity.Property(r => r.Nonce).HasMaxLength(64);
            entity.Property(r => r.PairingNumber).HasMaxLength(6);
            entity.Property(r => r.TokenHash).IsRequired();
            entity.HasIndex(r => r.TokenHash).IsUnique();
            entity.Property(r => r.RequestedAtUtc).HasConversion(unixMilliseconds);
            entity.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(r => r.Status);
        });

        modelBuilder.Entity<Pc>(entity =>
        {
            entity.Property(p => p.Name).HasMaxLength(32).UseCollation("NOCASE").IsRequired();
            entity.HasIndex(p => p.Name).IsUnique();
            entity.Property(p => p.TokenHash).IsRequired();
            entity.HasIndex(p => p.TokenHash).IsUnique();
            entity.Property(p => p.IpAddress).HasMaxLength(45);
            entity.Property(p => p.RegisteredAtUtc).HasConversion(unixMilliseconds);
            entity.Property(p => p.LastSeenUtc).HasConversion(unixMilliseconds);
            entity.Property(p => p.LoginLockedUntilUtc).HasConversion(unixMilliseconds);
            entity.Property(p => p.LastFailedLoginUtc).HasConversion(unixMilliseconds);
            entity.Property(p => p.LockedAtUtc).HasConversion(unixMilliseconds);
            entity.Property(p => p.LockMessage).HasMaxLength(ServerSettings.MaxLockMessageLength);
            entity.Property(p => p.ShutdownRequestedAtUtc).HasConversion(unixMilliseconds);
            entity.Property(p => p.ShutdownDeliveredAtUtc).HasConversion(unixMilliseconds);
            entity.Property(p => p.PowerAction).HasMaxLength(16);
            entity.Property(p => p.PowerCancelRequestedAtUtc).HasConversion(unixMilliseconds);
            entity.Property(p => p.ClientVersion).HasMaxLength(32);
            entity.Property(p => p.ClientUpdateTargetVersion).HasMaxLength(32);
            entity.Property(p => p.ClientUpdateState).HasMaxLength(16);
            entity.Property(p => p.ClientUpdateError).HasMaxLength(200);
            entity.Property(p => p.ClientUpdateStateAtUtc).HasConversion(unixMilliseconds);
        });

        modelBuilder.Entity<ClientRelease>(entity =>
        {
            entity.ToTable("ClientRelease", table => table.HasCheckConstraint("CK_ClientRelease_SingleRow", $"\"Id\" = {ClientRelease.SingletonId}"));
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Version).HasMaxLength(32).IsRequired();
            entity.Property(r => r.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(r => r.FileName).HasMaxLength(80).IsRequired();
            entity.Property(r => r.UploadedAtUtc).HasConversion(unixMilliseconds);
        });

        modelBuilder.Entity<PcSession>(entity =>
        {
            entity.Property(s => s.PcName).HasMaxLength(32).IsRequired();
            entity.Property(s => s.Username).HasMaxLength(64).IsRequired();
            entity.Property(s => s.StartedAtUtc).HasConversion(unixMilliseconds);
            entity.Property(s => s.ExpiresAtUtc).HasConversion(unixMilliseconds);
            entity.Property(s => s.EndedAtUtc).HasConversion(unixMilliseconds);
            entity.Property(s => s.PausedAtUtc).HasConversion(unixMilliseconds);
            entity.Property(s => s.EndReason).HasMaxLength(32);
            // At most one active (not ended) session per PC, enforced by the database.
            entity.HasIndex(s => s.PcId).IsUnique().HasFilter("\"EndedAtUtc\" IS NULL");
            entity.HasIndex(s => s.UserAccountId);
            // Finds the session an approved login request opened, to know whether it has ended.
            entity.HasIndex(s => s.LoginRequestId);
        });

        modelBuilder.Entity<ScreenViewEvent>(entity =>
        {
            entity.Property(e => e.AdminUsername).HasMaxLength(64).IsRequired();
            entity.Property(e => e.PcName).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Mode).HasMaxLength(16).IsRequired();
            entity.Property(e => e.StartedAtUtc).HasConversion(unixMilliseconds);
            entity.Property(e => e.EndedAtUtc).HasConversion(unixMilliseconds);
        });

        modelBuilder.Entity<ScreenControlEvent>(entity =>
        {
            entity.Property(e => e.AdminUserName).HasMaxLength(64).IsRequired();
            entity.Property(e => e.PcName).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Mode).HasMaxLength(16).IsRequired();
            entity.Property(e => e.StartedUtc).HasConversion(unixMilliseconds);
            entity.Property(e => e.EndedUtc).HasConversion(unixMilliseconds);
        });

        modelBuilder.Entity<PcMessage>(entity =>
        {
            entity.Property(m => m.PcName).HasMaxLength(32).IsRequired();
            entity.Property(m => m.AdminUserName).HasMaxLength(64).IsRequired();
            entity.Property(m => m.Text).HasMaxLength(500).IsRequired();
            entity.Property(m => m.CreatedUtc).HasConversion(unixMilliseconds);
            entity.Property(m => m.HandedOverUtc).HasConversion(unixMilliseconds);
            entity.Property(m => m.DeliveredUtc).HasConversion(unixMilliseconds);
            entity.HasIndex(m => new { m.PcId, m.CreatedUtc });
        });

        modelBuilder.Entity<ScreenBackground>(entity =>
        {
            entity.HasKey(b => b.Kind);
            entity.Property(b => b.Kind).HasMaxLength(16);
            entity.Property(b => b.ContentType).HasMaxLength(16).IsRequired();
            entity.Property(b => b.Data).IsRequired();
            entity.Property(b => b.Sha256).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<ServerSettings>(entity =>
        {
            entity.ToTable("ServerSettings", table => table.HasCheckConstraint("CK_ServerSettings_SingleRow", $"\"Id\" = {ServerSettings.SingletonId}"));
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.DefaultApprovalMode).HasMaxLength(16).IsRequired();
            entity.Property(s => s.NoticeText).HasMaxLength(ServerSettings.MaxNoticeLength).IsRequired();
            entity.Property(s => s.DefaultViewMode).HasMaxLength(16).IsRequired();
            // The default is also the value an existing settings row gets when this column is added.
            entity.Property(s => s.PowerCountdownSeconds).HasDefaultValue(ServerSettings.DefaultPowerCountdownSeconds);
            entity.Property(s => s.LockMessage).HasMaxLength(ServerSettings.MaxLockMessageLength).IsRequired().HasDefaultValue(ServerSettings.DefaultLockMessage);
        });
    }
}
