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

    // Set when the administrator asks for a shutdown or restart. Cleared when a heartbeat delivers it, or when it goes stale.
    public DateTimeOffset? ShutdownRequestedAtUtc { get; set; }
    // Set when a heartbeat delivers a shutdown or restart. Kept while the PC is off, so it shows as shut down or rebooting.
    public DateTimeOffset? ShutdownDeliveredAtUtc { get; set; }
    // "shutdown" or "restart": the action of the stored request or its delivery. Null on rows written before restarts
    // existed, which count as shutdowns. Read only while a request or delivery is stored.
    public string? PowerAction { get; set; }

    // Lockout for user logins attempted from this PC.
    public int FailedLoginCount { get; set; }
    // Failures older than this are forgotten, so the count doesn't build up across days.
    public DateTimeOffset? LastFailedLoginUtc { get; set; }
    public DateTimeOffset? LoginLockedUntilUtc { get; set; }
}
