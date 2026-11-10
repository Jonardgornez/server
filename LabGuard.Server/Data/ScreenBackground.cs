namespace LabGuard.Server.Data;

// The one background image for a screen the PCs show, shared by every PC. Kind is one of ScreenBackgroundKinds.
// Sha256 is the lowercase hex hash of Data, so a PC can tell whether its copy is current without downloading it.
public class ScreenBackground
{
    public string Kind { get; set; } = "";
    public string ContentType { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public string Sha256 { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
}

public static class ScreenBackgroundKinds
{
    public const string Login = "login";
    public const string Lock = "lock";

    public static bool IsKnown(string kind) => kind is Login or Lock;
}
