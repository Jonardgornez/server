namespace LabGuard.Server.Data;

// A desktop wallpaper the administrator can give PCs. One row per distinct image: Sha256 is the lowercase hex hash of Data,
// so the same bytes uploaded again share the row. A PC refers to it through Pc.WallpaperId.
public class Wallpaper
{
    public int Id { get; set; }
    public string ContentType { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public string Sha256 { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
}
