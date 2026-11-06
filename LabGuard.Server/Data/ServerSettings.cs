using LabGuard.Server.Accounts;

namespace LabGuard.Server.Data;

// The single settings row. Its Id is always SingletonId, enforced by a check constraint, and it is created with these defaults on first read.
public class ServerSettings
{
    public const int SingletonId = 1;
    public const int MaxNoticeLength = 300;
    public const int MinIdleTimeoutMinutes = 5;
    public const int MaxIdleTimeoutMinutes = 480;
    public const string DefaultNoticeText = "This computer may be monitored by the administrator.";
    public const string NotifiedViewMode = "notified";
    public const string AnonymousViewMode = "anonymous";

    public int Id { get; set; } = SingletonId;
    public string DefaultApprovalMode { get; set; } = AccountEndpoints.TimeLimitMode;
    public int DefaultApprovalMinutes { get; set; } = 60;
    public bool NoticeEnabled { get; set; } = true;
    public string NoticeText { get; set; } = DefaultNoticeText;
    public int IdleTimeoutMinutes { get; set; } = 30;
    public string DefaultViewMode { get; set; } = NotifiedViewMode;
}
