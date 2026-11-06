namespace LabGuard.Server.Data;

public class Pc
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // SHA-256 of the PC's token; the token itself is never stored.
    public string TokenHash { get; set; } = "";
    public string? IpAddress { get; set; }
    public DateTimeOffset RegisteredAtUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }

    // Set while the administrator has locked the PC. Survives restarts and the PC being offline.
    public DateTimeOffset? LockedAtUtc { get; set; }

    // Lockout for user logins attempted from this PC.
    public int FailedLoginCount { get; set; }
    // Failures older than this are forgotten, so the count doesn't build up across days.
    public DateTimeOffset? LastFailedLoginUtc { get; set; }
    public DateTimeOffset? LoginLockedUntilUtc { get; set; }
}
