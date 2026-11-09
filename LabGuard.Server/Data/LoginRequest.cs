namespace LabGuard.Server.Data;

// Stored as text, so a value added here is only ever appended at the end.
public enum LoginRequestStatus
{
    Pending,
    Approved,
    Denied,
    Cancelled,
}

public class LoginRequest
{
    public int Id { get; set; }
    public string PcName { get; set; } = "";

    // The PC that asked. No foreign key, so the request outlives the PC. Null for the development samples.
    public int? PcId { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public LoginRequestStatus Status { get; set; }

    // For an approved request, null means open time. Always null for pending and denied requests.
    public int? ApprovedMinutes { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }

    // Why the request was closed without an approval (see LoginRequestLifecycle). Null for a plain deny, and while pending or approved.
    public string? Reason { get; set; }
}
