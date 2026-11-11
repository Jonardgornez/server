namespace LabGuard.Server.Data;

// The latest status report of one PC's background service, kept with the server's receive time. One row per PC, removed with the PC.
// The report doesn't change LastSeenUtc, which the client heartbeat owns. EventsJson holds the events as a JSON array of ServiceReportEvent.
public class PcServiceReport
{
    public int PcId { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public string? ServiceVersion { get; set; }
    public bool ClientRunning { get; set; }
    public string? ClientVersion { get; set; }
    public bool UserSignedIn { get; set; }
    public string? UpdateVersion { get; set; }
    public string? UpdateState { get; set; }
    public string? UpdateError { get; set; }
    public string EventsJson { get; set; } = "[]";
}

// One event the service reported. Level is information, warning or error.
public sealed record ServiceReportEvent(DateTimeOffset At, string Level, string Message);
