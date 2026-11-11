using System.Globalization;
using System.Text.Json;
using LabGuard.Server.ClientUpdates;
using LabGuard.Server.Data;

namespace LabGuard.Server.Clients;

// What a service report says once read. Anything invalid is null, false or left out, so the rest of the report is still kept.
internal sealed record ServiceReportValues(
    string? ServiceVersion,
    bool ClientRunning,
    ReleaseVersion? ClientVersion,
    bool UserSignedIn,
    ServiceReportUpdate? Update,
    IReadOnlyList<ServiceReportEvent> Events);

// The update part of a service report. Each field is null when it is missing or invalid. Null as a whole when the report has no update.
internal sealed record ServiceReportUpdate(string? Version, string? State, string? Error);

// Reads a service report the way ClientUpdateHeartbeat reads a heartbeat: a part that is wrongly typed or invalid is ignored
// and never refuses the report. Only a body that isn't a JSON object is refused, before this runs.
internal static class ServiceReportReader
{
    public const int MaxEvents = 20;
    public const int MaxEventMessageLength = 300;

    // The service reports every state of an update, where the heartbeat reports only the ones from downloading on.
    private static readonly string[] UpdateStates = ["pending", "waiting", "downloading", "installing", "failed"];
    private static readonly string[] EventLevels = ["information", "warning", "error"];

    public static ServiceReportValues Read(ServiceReportRequest request) =>
        new(
            ClientUpdateHeartbeat.ReadVersion(request.ServiceVersion)?.ToString(),
            IsTrue(request.ClientRunning),
            ClientUpdateHeartbeat.ReadVersion(request.ClientVersion),
            IsTrue(request.UserSignedIn),
            ReadUpdate(request.Update),
            ReadEvents(request.Events));

    private static bool IsTrue(JsonElement? element) => element is { ValueKind: JsonValueKind.True };

    private static ServiceReportUpdate? ReadUpdate(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } update)
        {
            return null;
        }

        var version = ReleaseVersion.TryParse(ClientUpdateHeartbeat.ReadString(update, "version"), out var parsed) ? parsed.ToString() : null;
        var state = ClientUpdateHeartbeat.ReadString(update, "state");
        var error = ClientUpdateHeartbeat.CleanError(ClientUpdateHeartbeat.ReadString(update, "error"));
        var knownState = state is not null && UpdateStates.Contains(state) ? state : null;
        return version is null && knownState is null && error is null
            ? null
            : new ServiceReportUpdate(version, knownState, error);
    }

    // The first MaxEvents readable events are kept, in the order the service sent them. An event with an unknown level, a time
    // that doesn't parse, or no text left is left out.
    private static IReadOnlyList<ServiceReportEvent> ReadEvents(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } events)
        {
            return [];
        }

        var kept = new List<ServiceReportEvent>();
        foreach (var item in events.EnumerateArray())
        {
            if (kept.Count == MaxEvents)
            {
                break;
            }

            if (ReadEvent(item) is { } readable)
            {
                kept.Add(readable);
            }
        }

        return kept;
    }

    private static ServiceReportEvent? ReadEvent(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var level = ClientUpdateHeartbeat.ReadString(element, "level");
        var at = ClientUpdateHeartbeat.ReadString(element, "at");
        var message = ClientUpdateHeartbeat.CleanText(ClientUpdateHeartbeat.ReadString(element, "message"), MaxEventMessageLength);
        if (level is null
            || !EventLevels.Contains(level)
            || at is null
            || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            || message is null)
        {
            return null;
        }

        return new ServiceReportEvent(time.ToUniversalTime(), level, message);
    }
}
