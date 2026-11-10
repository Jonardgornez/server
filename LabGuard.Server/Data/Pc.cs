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
    // The countdown the delivery was given, kept with it so a later change to the setting never alters a shutdown or restart
    // under way. Null on rows delivered before it was stored, which use the default.
    public int? PowerCountdownSeconds { get; set; }
    // Set when an administrator cancels a delivered shutdown or restart while the PC is online. Cleared when the PC
    // acknowledges the cancel, or with the delivery, so a cancel never outlives its delivery.
    public DateTimeOffset? PowerCancelRequestedAtUtc { get; set; }

    // What the administrator wants for this PC's internet access: true blocks it. The PC applies it from its next heartbeat.
    public bool InternetBlocked { get; set; }
    // What the PC last reported it applied. Null until a client that reports it has sent a heartbeat, so older clients show as unknown.
    public bool? InternetBlockApplied { get; set; }

    // Lockout for user logins attempted from this PC.
    public int FailedLoginCount { get; set; }
    // Failures older than this are forgotten, so the count doesn't build up across days.
    public DateTimeOffset? LastFailedLoginUtc { get; set; }
    public DateTimeOffset? LoginLockedUntilUtc { get; set; }

    // The client version the PC last reported. Null until a client that reports it has sent a heartbeat.
    public string? ClientVersion { get; set; }
    // The version the administrator has offered this PC. Null when no update is pending.
    public string? ClientUpdateTargetVersion { get; set; }
    // What the PC reported about its pending update: "downloading", "installing" or "failed". Null until it reports one.
    public string? ClientUpdateState { get; set; }
    public string? ClientUpdateError { get; set; }
    public DateTimeOffset? ClientUpdateStateAtUtc { get; set; }
}
