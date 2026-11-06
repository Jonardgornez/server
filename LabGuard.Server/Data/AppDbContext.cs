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
            entity.HasIndex(r => r.Status);
        });

        modelBuilder.Entity<PcRegistrationRequest>(entity =>
        {
            entity.Property(r => r.Name).HasMaxLength(32).IsRequired();
            entity.Property(r => r.IpAddress).HasMaxLength(45);
            entity.Property(r => r.Nonce).HasMaxLength(64).IsRequired();
            entity.Property(r => r.PairingNumber).HasMaxLength(6).IsRequired();
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
        });

        modelBuilder.Entity<ServerSettings>(entity =>
        {
            entity.ToTable("ServerSettings", table => table.HasCheckConstraint("CK_ServerSettings_SingleRow", $"\"Id\" = {ServerSettings.SingletonId}"));
            entity.Property(s => s.Id).ValueGeneratedNever();
            entity.Property(s => s.DefaultApprovalMode).HasMaxLength(16).IsRequired();
            entity.Property(s => s.NoticeText).HasMaxLength(ServerSettings.MaxNoticeLength).IsRequired();
            entity.Property(s => s.DefaultViewMode).HasMaxLength(16).IsRequired();
        });
    }
}
