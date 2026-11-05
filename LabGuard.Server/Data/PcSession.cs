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
}
