using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Admin shutdown of one PC or many. The server only records the request. The PC picks it up on its next heartbeat.
public static partial class PcEndpoints
{
    private const string ServerPcShutdownMessage = "This PC runs the server. Shut it down by hand.";

    private static async Task<IResult> ShutdownAsync(
        int id,
        HttpContext http,
        AppDbContext db,
        TimeProvider clock,
        ILocalAddressSource localAddresses)
    {
        if (await AuthEndpoints.ReadBodyAsync<PcAdminActionRequest>(http.Request) is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        // The same order as the lock: closing expired sessions first, then the PC, all in one transaction.
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SessionLifecycle.EndExpiredAsync(db, now, ct);
        var pc = await db.Pcs.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pc is null)
        {
            return PcNotFound();
        }

        if (!IsOnline(pc.LastSeenUtc, now))
        {
            return ConflictResponse("This PC is offline.");
        }

        if (ServerPcRule.IsServerPc(pc.IpAddress, localAddresses.Read()))
        {
            return ConflictResponse(ServerPcShutdownMessage);
        }

        // A PC already pending is left as it is, so the request is not refreshed.
        await RequestShutdownsAsync(db, [id], now, ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ShutdownManyAsync(
        HttpContext http,
        AppDbContext db,
        TimeProvider clock,
        ILocalAddressSource localAddresses)
    {
        var pcIds = await ReadPcIdsAsync(http.Request);
        if (pcIds is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        var machineAddresses = localAddresses.Read();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SessionLifecycle.EndExpiredAsync(db, now, ct);

        // Offline PCs and the PC running this server are skipped. Unknown ids match nothing.
        var shutdownIds = (await db.Pcs
            .AsNoTracking()
            .Where(p => pcIds.Contains(p.Id))
            .Select(p => new { p.Id, p.LastSeenUtc, p.IpAddress })
            .ToListAsync(ct))
            .Where(p => IsOnline(p.LastSeenUtc, now) && !ServerPcRule.IsServerPc(p.IpAddress, machineAddresses))
            .Select(p => p.Id)
            .ToArray();
        var requested = await RequestShutdownsAsync(db, shutdownIds, now, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new AffectedPcsResponse(requested));
    }

    // Requests a shutdown for each listed PC that isn't already pending. A request that went stale is replaced.
    // Returns how many PCs were newly requested.
    private static Task<int> RequestShutdownsAsync(AppDbContext db, int[] pcIds, DateTimeOffset now, CancellationToken ct)
    {
        var expiredBefore = now - PcShutdownRules.RequestWindow;
        return db.Pcs
            .Where(p => pcIds.Contains(p.Id) && (p.ShutdownRequestedAtUtc == null || p.ShutdownRequestedAtUtc <= expiredBefore))
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)now), ct);
    }
}
