using System.Text.Json;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.ClientUpdates;

// The update a heartbeat offers a PC. The PC downloads the installer with this hash and checks it before running it.
// InstallNow is true when the administrator chose to install even while the PC is in use, after a warning to the student.
public record ClientUpdateOffer(string Version, string Sha256, long SizeBytes, bool InstallNow);

// What a PC reports about its update: the version it is updating to, its state and its error. Only a report for the stored target is recorded.
internal sealed record ClientUpdateReport(string Version, string State, string? Error);

// What the PC reports on its heartbeat about its update. Invalid parts are ignored, so an odd client keeps heartbeating.
public static class ClientUpdateHeartbeat
{
    public const int MaxErrorLength = 200;

    private static readonly string[] ReportedStates = ["downloading", "installing", "failed"];

    // The update states a client reports on its heartbeat. Progress in any other state is never recorded on the PC.
    internal static bool IsReportedState(string state) => ReportedStates.Contains(state);

    // Records the version and update progress the PC reports, clears a target that is done or no longer the stored release,
    // and returns the offer when the PC still needs the stored release.
    public static async Task<ClientUpdateOffer?> HandleAsync(
        AppDbContext db,
        int pcId,
        HeartbeatRequest? request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The stored version always describes the client sending this heartbeat. A client older than 0.20.0 sends none, so a heartbeat
        // without a readable version makes it unknown: otherwise a PC moved to an older client would keep showing the newer version.
        await StoreVersionAsync(db, pcId, ReadVersion(request?.ClientVersion)?.ToString(), cancellationToken);
        return await OfferAsync(db, pcId, ReadReport(request?.ClientUpdate), now, cancellationToken);
    }

    // The service report is a second source of the same client's version and update. A readable version is stored. An unreadable one
    // leaves the stored version alone: the heartbeat is the source that sets it, and a report without one doesn't say the client changed.
    internal static async Task<ClientUpdateOffer?> HandleServiceReportAsync(
        AppDbContext db,
        int pcId,
        ReleaseVersion? clientVersion,
        ClientUpdateReport? report,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (clientVersion is { } version)
        {
            await StoreVersionAsync(db, pcId, version.ToString(), cancellationToken);
        }

        return await OfferAsync(db, pcId, report, now, cancellationToken);
    }

    // Written only when it changed, so an ordinary heartbeat doesn't write the row.
    private static async Task StoreVersionAsync(AppDbContext db, int pcId, string? version, CancellationToken cancellationToken)
    {
        await db.Pcs
            .Where(p => p.Id == pcId && p.ClientVersion != version)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ClientVersion, version), cancellationToken);
    }

    private static async Task<ClientUpdateOffer?> OfferAsync(
        AppDbContext db,
        int pcId,
        ClientUpdateReport? report,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pc = await db.Pcs
            .AsNoTracking()
            .Where(p => p.Id == pcId)
            .Select(p => new { p.ClientVersion, p.ClientUpdateTargetVersion, p.ClientUpdateState, p.ClientUpdateError, p.ClientUpdateInstallNow })
            .FirstOrDefaultAsync(cancellationToken);
        if (pc?.ClientUpdateTargetVersion is not { } target)
        {
            return null;
        }

        var release = await db.ClientReleases
            .AsNoTracking()
            .Select(r => new { r.Version, r.Sha256, r.SizeBytes })
            .FirstOrDefaultAsync(cancellationToken);
        // A target is only ever the stored release. One that isn't (the release was replaced or removed) is cleared, never offered.
        if (release is null || release.Version != target)
        {
            await ClientUpdateTargets.ClearWhereAsync(db, p => p.Id == pcId && p.ClientUpdateTargetVersion == target, cancellationToken);
            return null;
        }

        if (pc.ClientVersion is { } stored && ReleaseVersion.TryParse(stored, out var current) && current >= ReleaseVersion.Parse(target))
        {
            // The PC is at or past the target, so the update is done. Only this target is cleared, so a newer target set meanwhile survives.
            await ClientUpdateTargets.ClearWhereAsync(db, p => p.Id == pcId && p.ClientUpdateTargetVersion == target, cancellationToken);
            return null;
        }

        if (report is { } progress && progress.Version == target && (progress.State != pc.ClientUpdateState || progress.Error != pc.ClientUpdateError))
        {
            await db.Pcs
                .Where(p => p.Id == pcId && p.ClientUpdateTargetVersion == target)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.ClientUpdateState, progress.State)
                    .SetProperty(p => p.ClientUpdateError, progress.Error)
                    .SetProperty(p => p.ClientUpdateStateAtUtc, (DateTimeOffset?)now), cancellationToken);
        }

        return new ClientUpdateOffer(release.Version, release.Sha256, release.SizeBytes, pc.ClientUpdateInstallNow);
    }

    // A version that isn't major.minor.patch is treated as not reported.
    internal static ReleaseVersion? ReadVersion(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } text && ReleaseVersion.TryParse(text.GetString(), out var version)
            ? version
            : null;

    // The report is ignored when its state or version is invalid. The error text is cleaned up, never refused, and the state still counts.
    private static ClientUpdateReport? ReadReport(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } report)
        {
            return null;
        }

        if (ReadString(report, "state") is not { } state || !IsReportedState(state))
        {
            return null;
        }

        if (!ReleaseVersion.TryParse(ReadString(report, "version"), out var version))
        {
            return null;
        }

        return new ClientUpdateReport(version.ToString(), state, CleanError(ReadString(report, "error")));
    }

    // Plain text only, capped at the error length.
    internal static string? CleanError(string? text) => CleanText(text, MaxErrorLength);

    // Plain text only: control, invisible formatting and bidi characters are stripped, the text is trimmed and capped at the length.
    // The cap never splits a surrogate pair. Null when nothing is left.
    internal static string? CleanText(string? text, int maxLength)
    {
        if (text is null)
        {
            return null;
        }

        var plain = new string(text.Where(character => !IsStrippedCharacter(character)).ToArray()).Trim();
        if (plain.Length > maxLength)
        {
            var cut = maxLength;
            if (char.IsHighSurrogate(plain[cut - 1]))
            {
                cut--;
            }

            plain = plain[..cut];
        }

        return plain.Length == 0 ? null : plain;
    }

    // C0 and C1 controls (DEL included), bidi marks and overrides, isolates, zero-width characters, line and paragraph separators, and the byte-order mark.
    private static bool IsStrippedCharacter(char character) =>
        character <= 0x1F
        || character is >= '\u007F' and <= '\u009F'
        || character == '\u061C'
        || character is >= '\u200B' and <= '\u200F'
        || character is '\u2028' or '\u2029'
        || character is >= '\u202A' and <= '\u202E'
        || character is >= '\u2066' and <= '\u2069'
        || character == '\uFEFF';

    internal static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
