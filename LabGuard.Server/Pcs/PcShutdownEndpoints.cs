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
    private const string AddressUnreadableMessage = "Couldn't check this PC's address. Try again.";

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

        // Fails closed: without the addresses, this PC can't be shown not to be the server.
        if (localAddresses.ReadOrNull() is not { } machineAddresses)
        {
            return ConflictResponse(AddressUnreadableMessage);
        }

        if (ServerPcRule.IsServerPc(pc.IpAddress, machineAddresses))
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

        // Without the addresses no PC can be shown not to be the server, so nothing is requested.
        if (localAddresses.ReadOrNull() is not { } machineAddresses)
        {
            return Results.Ok(new AffectedPcsResponse(0));
        }

        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
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
    // A PC shut down within the delivery window is skipped, so a fresh delivery isn't followed by a second request.
    // Returns how many PCs were newly requested.
    private static Task<int> RequestShutdownsAsync(AppDbContext db, int[] pcIds, DateTimeOffset now, CancellationToken ct)
    {
        var expiredBefore = now - PcShutdownRules.RequestWindow;
        var deliveredBefore = now - PcShutdownRules.DeliveryWindow;
        return db.Pcs
            .Where(p => pcIds.Contains(p.Id)
                && (p.ShutdownRequestedAtUtc == null || p.ShutdownRequestedAtUtc <= expiredBefore)
                && (p.ShutdownDeliveredAtUtc == null || p.ShutdownDeliveredAtUtc <= deliveredBefore))
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)now), ct);
    }
}
