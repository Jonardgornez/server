namespace LabGuard.Server.Data;

// One period of remote control, from the PC's confirmation until control leaves on. Kept for the audit trail; nothing reads it yet.
// AdminUserName and PcName are copies, so the record survives a removed PC or account. No input or frame is stored here.
public class ScreenControlEvent
{
    public int Id { get; set; }
    public string AdminUserName { get; set; } = "";
    public string PcName { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }
}
