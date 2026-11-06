namespace LabGuard.Server.Data;

// One live screen view, from the moment an administrator opened it until it closed. Kept for the audit trail; nothing reads it yet.
// AdminUsername and PcName are copies, so the record survives a removed PC or account. No frame or view id is stored here.
public class ScreenViewEvent
{
    public int Id { get; set; }
    public string AdminUsername { get; set; } = "";
    public string PcName { get; set; } = "";
    public string Mode { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
}
