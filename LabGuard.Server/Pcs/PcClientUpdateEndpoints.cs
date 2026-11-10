using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using LabGuard.Server.Auth;
using LabGuard.Server.ClientUpdates;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Sent by the administrator. PcIds names the PCs, and Version is the stored release the administrator saw. Both are required for an offer.
public record PcClientUpdateOfferRequest(int[]? PcIds, string? Version);
// Sent by the administrator to cancel. PcIds names the PCs.
public record PcClientUpdateCancelRequest(int[]? PcIds);
// A pending update as the administrator sees it. State is "pending" until the PC reports progress.
public record PcClientUpdate(string TargetVersion, string State, string? Error);

public static partial class PcEndpoints
{
    // Most PCs one request may name. The lab has 60, so this only keeps a single request bounded.
    public const int MaxClientUpdatePcIds = 500;

    private const string ReleaseChangedMessage = "The uploaded update changed. Check the version and try again.";

    // Offers the stored release to each listed PC that reports an older version or none. The PC installs it only when idle.
    // An offline PC gets the offer too, on its next heartbeat. The version must be the stored one, so the administrator
    // never offers a release other than the one they were shown.
    private static async Task<IResult> OfferClientUpdateAsync(
        HttpContext http,
        ClaimsPrincipal user,
        AppDbContext db,
        TimeProvider clock,
        ILogger<ClientUpdateLog> logger)
    {
        var request = await AuthEndpoints.ReadBodyAsync<PcClientUpdateOfferRequest>(http.Request);
        if (request is null || !IsValidClientUpdateIds(request.PcIds) || !ReleaseVersion.TryParse(request.Version, out _))
        {
            return AuthEndpoints.InvalidRequest();
        }

        var pcIds = request.PcIds;
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        // The release is read inside the transaction, and the targets are written only while that same release is stored.
        // A replacement that lands in between therefore can't receive a target for the version it just replaced.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var release = await db.ClientReleases.AsNoTracking().Select(r => r.Version).FirstOrDefaultAsync(ct);
        if (release is null)
        {
            return ConflictResponse(ClientUpdateEndpoints.NoReleaseMessage);
        }

        if (request.Version != release)
        {
            return ConflictResponse(ReleaseChangedMessage);
        }

        var releaseVersion = ReleaseVersion.Parse(release);
        var reported = await db.Pcs
            .AsNoTracking()
            .Where(p => pcIds.Contains(p.Id))
            .Select(p => new { p.Id, p.ClientVersion })
            .ToListAsync(ct);
        var behindIds = reported
            .Where(p => IsBehind(p.ClientVersion, releaseVersion))
            .Select(p => p.Id)
            .ToArray();
        var affected = await db.Pcs
            .Where(p => behindIds.Contains(p.Id) && db.ClientReleases.Any(r => r.Version == release))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.ClientUpdateTargetVersion, release)
                .SetProperty(p => p.ClientUpdateState, (string?)null)
                .SetProperty(p => p.ClientUpdateError, (string?)null)
                .SetProperty(p => p.ClientUpdateStateAtUtc, (DateTimeOffset?)now), ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Client update {Version} offered to {Affected} PC(s) by {Administrator}.",
            release,
            affected,
            ClientUpdateEndpoints.AdministratorName(user));
        return Results.Ok(new AffectedPcsResponse(affected));
    }

    // Clears the pending update and anything the PC reported about it, whatever its state. Idempotent: a PC with nothing to clear is not counted.
    private static async Task<IResult> CancelClientUpdateAsync(
        HttpContext http,
        ClaimsPrincipal user,
        AppDbContext db,
        ILogger<ClientUpdateLog> logger)
    {
        var request = await AuthEndpoints.ReadBodyAsync<PcClientUpdateCancelRequest>(http.Request);
        if (request is null || !IsValidClientUpdateIds(request.PcIds))
        {
            return AuthEndpoints.InvalidRequest();
        }

        var pcIds = request.PcIds;
        var ct = http.RequestAborted;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var affected = await ClientUpdateTargets.ClearWhereAsync(
            db,
            p => pcIds.Contains(p.Id)
                && (p.ClientUpdateTargetVersion != null || p.ClientUpdateState != null || p.ClientUpdateError != null || p.ClientUpdateStateAtUtc != null),
            ct);
        await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Client update cancelled for {Affected} PC(s) by {Administrator}.",
            affected,
            ClientUpdateEndpoints.AdministratorName(user));
        return Results.Ok(new AffectedPcsResponse(affected));
    }

    // Behind means an unknown version, or one lower than the release. A stored version that can't be read counts as unknown.
    private static bool IsBehind(string? reportedVersion, ReleaseVersion releaseVersion) =>
        reportedVersion is null
        || !ReleaseVersion.TryParse(reportedVersion, out var version)
        || version < releaseVersion;

    // Each id is a positive PC id, listed once, and the list has at least one and at most the limit.
    private static bool IsValidClientUpdateIds([NotNullWhen(true)] int[]? pcIds) =>
        pcIds is { Length: > 0 and <= MaxClientUpdatePcIds } && pcIds.All(id => id > 0) && pcIds.Distinct().Count() == pcIds.Length;
}
