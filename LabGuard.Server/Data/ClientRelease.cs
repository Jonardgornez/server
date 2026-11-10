namespace LabGuard.Server.Data;

// The one installer every PC can be updated to. FileName is the server-chosen name of the stored file, never a client's name.
public class ClientRelease
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string FileName { get; set; } = "";
    public DateTimeOffset UploadedAtUtc { get; set; }
}
