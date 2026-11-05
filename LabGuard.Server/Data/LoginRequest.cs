namespace LabGuard.Server.Data;

public enum LoginRequestStatus
{
    Pending,
    Approved,
    Denied,
}

public class LoginRequest
{
    public int Id { get; set; }
    public string PcName { get; set; } = "";
    public DateTimeOffset RequestedAtUtc { get; set; }
    public LoginRequestStatus Status { get; set; }

    // For an approved request, null means open time. Always null for pending and denied requests.
    public int? ApprovedMinutes { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
}
