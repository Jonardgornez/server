namespace LabGuard.Server.Data;

public enum RegistrationRequestStatus
{
    Pending,
    Approved,
    Denied,
    Cancelled,
}

// A new lab PC asks to join. The administrator accepts or denies it; accepting creates the Pc with TokenHash,
// so the PC already holds the token it was given here. Only the token's hash is stored.
public class PcRegistrationRequest
{
    // A pending request older than this counts as expired.
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);

    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? IpAddress { get; set; }

    // 32 random bytes from the PC, as 64 lowercase hex characters. Part of the pairing number.
    public string Nonce { get; set; } = "";

    // Six digits both screens show, so the administrator can check they match before accepting.
    public string PairingNumber { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset RequestedAtUtc { get; set; }
    public RegistrationRequestStatus Status { get; set; }

    // The oldest time a pending request can have and still be unexpired.
    public static DateTimeOffset PendingSince(DateTimeOffset now) => now - PendingLifetime;
}
