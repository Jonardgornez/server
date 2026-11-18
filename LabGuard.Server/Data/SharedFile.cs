namespace LabGuard.Server.Data;

// A file the administrator uploaded to send to PCs. One row per distinct file: Sha256 is the lowercase hex hash of its bytes,
// so the same bytes uploaded again share the row. The bytes are kept on disk under the hash (SharedFileStorage), never under
// a name the administrator chose. Name is the cleaned name of the latest upload of these bytes.
public class SharedFile
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
}
