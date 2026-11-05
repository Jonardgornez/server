namespace LabGuard.Server.Data;

public class UserAccount
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; }

    // Null means open time (no limit).
    public int? DefaultTimeMinutes { get; set; }
    public DateTimeOffset? LastLoginUtc { get; set; }
}
