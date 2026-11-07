namespace LabGuard.Server.Data;

// One message an administrator sent to one PC. Kept for the audit trail for 90 days (PcMessageRetention removes older rows at startup).
// PcName and AdminUserName are copies, so the record survives a removed PC or account. PcId is not a foreign key for that reason.
// HandedOverUtc is set when a heartbeat hands the message to the PC, once. DeliveredUtc is set when the PC acknowledges it in time.
public class PcMessage
{
    public long Id { get; set; }
    public int PcId { get; set; }
    public string PcName { get; set; } = "";
    public string AdminUserName { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? HandedOverUtc { get; set; }
    public DateTimeOffset? DeliveredUtc { get; set; }
}
