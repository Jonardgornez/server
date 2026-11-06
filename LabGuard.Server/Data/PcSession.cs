namespace LabGuard.Server.Data;

// PcId and UserAccountId are plain values with no foreign keys, so the history survives
// when a PC or an account is removed. Name and username are snapshots for the same reason.
public class PcSession
{
    public int Id { get; set; }
    public int PcId { get; set; }
    public string PcName { get; set; } = "";
    public int UserAccountId { get; set; }
    public string Username { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }

    // Null means open time (no limit).
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    // The default time that was granted when the session started; null for open time.
    public int? DefaultTimeMinutes { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public string? EndReason { get; set; }

    // Set while the PC is locked. A paused session never expires; unlocking moves its expiry forward by the pause.
    public DateTimeOffset? PausedAtUtc { get; set; }

    // True for a session the admin opened to unlock the PC. It has no account and never expires.
    public bool IsAdminUnlock { get; set; }
}
