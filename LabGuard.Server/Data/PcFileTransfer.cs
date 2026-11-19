namespace LabGuard.Server.Data;

// One file sent to one PC, and how far the PC has got with it. FileName, SizeBytes and Sha256 are copies taken when the file
// was sent, so the record still reads the same once the shared file's bytes are removed (SharedFileId is then null).
// PcName is a copy too. Status is one of the SharedFileRules status strings, and Decision is the administrator's answer when
// the file already existed on the PC. The row is removed with its PC, and 24 hours after it was created.
public class PcFileTransfer
{
    public long Id { get; set; }
    public int PcId { get; set; }
    public string PcName { get; set; } = "";
    public int? SharedFileId { get; set; }
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Subfolder { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Decision { get; set; }
    public string? SavedAs { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    // How many times a heartbeat has handed the transfer to the PC, the first time included. Bounds handing an abandoned
    // transfer over again.
    public int HandOverCount { get; set; }
}
