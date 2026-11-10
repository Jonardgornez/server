using System.Text.Json;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.ClientUpdates;

// The update a heartbeat offers a PC. The PC downloads the installer with this hash and checks it before running it.
public record ClientUpdateOffer(string Version, string Sha256, long SizeBytes);

// What the PC reports on its heartbeat about its update. Invalid parts are ignored, so an odd client keeps heartbeating.
public static class ClientUpdateHeartbeat
{
    public const int MaxErrorLength = 200;

    private static readonly string[] ReportedStates = ["downloading", "installing", "failed"];

    // Records the version and update progress the PC reports, clears a target that is done or no longer the stored release,
    // and returns the offer when the PC still needs the stored release.
    public static async Task<ClientUpdateOffer?> HandleAsync(
        AppDbContext db,
        int pcId,
        HeartbeatRequest? request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var reportedVersion = ReadVersion(request?.ClientVersion);
        if (reportedVersion is { } version)
        {
            // Written only when it changed, so an ordinary heartbeat doesn't write the row.
            var text = version.ToString();
            await db.Pcs
                .Where(p => p.Id == pcId && (p.ClientVersion == null || p.ClientVersion != text))
                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ClientVersion, text), cancellationToken);
        }

        var pc = await db.Pcs
            .AsNoTracking()
            .Where(p => p.Id == pcId)
            .Select(p => new { p.ClientVersion, p.ClientUpdateTargetVersion, p.ClientUpdateState, p.ClientUpdateError })
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

        var report = ReadReport(request?.ClientUpdate);
        if (report is { } progress && progress.Version == target && (progress.State != pc.ClientUpdateState || progress.Error != pc.ClientUpdateError))
        {
            await db.Pcs
                .Where(p => p.Id == pcId && p.ClientUpdateTargetVersion == target)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.ClientUpdateState, progress.State)
                    .SetProperty(p => p.ClientUpdateError, progress.Error)
                    .SetProperty(p => p.ClientUpdateStateAtUtc, (DateTimeOffset?)now), cancellationToken);
        }

        return new ClientUpdateOffer(release.Version, release.Sha256, release.SizeBytes);
    }

    // A version that isn't major.minor.patch is treated as not reported.
    private static ReleaseVersion? ReadVersion(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } text && ReleaseVersion.TryParse(text.GetString(), out var version)
            ? version
            : null;

    private sealed record ClientUpdateReport(string Version, string State, string? Error);

    // The report is ignored when its state or version is invalid. The error text is cleaned up, never refused, and the state still counts.
    private static ClientUpdateReport? ReadReport(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } report)
        {
            return null;
        }

        if (ReadString(report, "state") is not { } state || !ReportedStates.Contains(state))
        {
            return null;
        }

        if (!ReleaseVersion.TryParse(ReadString(report, "version"), out var version))
        {
            return null;
        }

        return new ClientUpdateReport(version.ToString(), state, CleanError(ReadString(report, "error")));
    }

    // Plain text only: control, invisible formatting and bidi characters are stripped, the text is trimmed and capped at the limit.
    // The cap never splits a surrogate pair. Null when nothing is left.
    internal static string? CleanError(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var plain = new string(text.Where(character => !IsStrippedCharacter(character)).ToArray()).Trim();
        if (plain.Length > MaxErrorLength)
        {
            var cut = MaxErrorLength;
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

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
