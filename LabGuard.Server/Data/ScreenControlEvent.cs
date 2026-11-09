namespace LabGuard.Server.Data;

// One period of remote control, from the PC's confirmation until control leaves on. Kept for the audit trail; nothing reads it yet.
// AdminUserName and PcName are copies, so the record survives a removed PC or account. No input or frame is stored here.
// Mode is the view mode the control was taken from: ScreenProtocol.NotifiedMode or ScreenProtocol.AnonymousMode.
public class ScreenControlEvent
{
    public int Id { get; set; }
    public string AdminUserName { get; set; } = "";
    public string PcName { get; set; } = "";
    public string Mode { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }
}
