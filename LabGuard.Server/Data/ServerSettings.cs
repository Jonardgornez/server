using LabGuard.Server.Accounts;

namespace LabGuard.Server.Data;

// The single settings row. Its Id is always SingletonId, enforced by a check constraint, and it is created with these defaults on first read.
public class ServerSettings
{
    public const int SingletonId = 1;
    public const int MaxNoticeLength = 300;
    public const int MaxLockMessageLength = 200;
    // An idle timeout of 0 means the admin stays signed in until they sign out.
    public const int NeverIdleTimeoutMinutes = 0;
    public const int MinIdleTimeoutMinutes = 5;
    public const int MaxIdleTimeoutMinutes = 480;
    public const int DefaultPowerCountdownSeconds = 30;
    public const int MinPowerCountdownSeconds = 5;
    public const int MaxPowerCountdownSeconds = 3600;
    public const string DefaultNoticeText = "This computer may be monitored by the administrator.";
    public const string DefaultLockMessage = "This computer has been locked by the administrator.";
    public const string NotifiedViewMode = "notified";
    public const string AnonymousViewMode = "anonymous";

    public int Id { get; set; } = SingletonId;
    public string DefaultApprovalMode { get; set; } = AccountEndpoints.TimeLimitMode;
    public int DefaultApprovalMinutes { get; set; } = 60;
    public bool NoticeEnabled { get; set; } = true;
    public string NoticeText { get; set; } = DefaultNoticeText;
    public int IdleTimeoutMinutes { get; set; } = NeverIdleTimeoutMinutes;
    public string DefaultViewMode { get; set; } = NotifiedViewMode;
    public int PowerCountdownSeconds { get; set; } = DefaultPowerCountdownSeconds;
    // Shown on a locked PC that has no message of its own.
    public string LockMessage { get; set; } = DefaultLockMessage;
    // The one uninstall password of all PCs, kept only as a PBKDF2 hash. All three are null while no password is set.
    public string? UninstallPasswordHash { get; set; }
    public string? UninstallPasswordSalt { get; set; }
    public int? UninstallPasswordIterations { get; set; }
}
