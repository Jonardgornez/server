using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Admin shutdown and reboot of one PC or many. The server only records the request. The PC picks it up on its next heartbeat.
public static partial class PcEndpoints
{
    private const string AddressUnreadableMessage = "Couldn't check this PC's address. Try again.";

    private static Task<IResult> ShutdownAsync(int id, HttpContext http, AppDbContext db, TimeProvider clock, ILocalAddressSource localAddresses) =>
        RequestPowerActionAsync(id, PcShutdownRules.ShutdownAction, http, db, clock, localAddresses);

    private static Task<IResult> RebootAsync(int id, HttpContext http, AppDbContext db, TimeProvider clock, ILocalAddressSource localAddresses) =>
        RequestPowerActionAsync(id, PcShutdownRules.RestartAction, http, db, clock, localAddresses);

    private static Task<IResult> ShutdownManyAsync(HttpContext http, AppDbContext db, TimeProvider clock, ILocalAddressSource localAddresses) =>
        RequestPowerActionManyAsync(PcShutdownRules.ShutdownAction, http, db, clock, localAddresses);

    private static Task<IResult> RebootManyAsync(HttpContext http, AppDbContext db, TimeProvider clock, ILocalAddressSource localAddresses) =>
        RequestPowerActionManyAsync(PcShutdownRules.RestartAction, http, db, clock, localAddresses);

    private static async Task<IResult> RequestPowerActionAsync(
        int id,
        string action,
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
            return ConflictResponse(ServerPcMessage(action));
        }

        // A PC already pending is left as it is, so the request is not refreshed and its first action is kept.
        var requested = await RequestPowerActionsAsync(db, [id], action, now, ct);
        await transaction.CommitAsync(ct);

        // Repeating the action in progress is fine. Asking for the other one is refused, so nobody is told it will restart when it will shut down.
        return requested == 0 && PcShutdownRules.ActionOf(pc.PowerAction) != action
            ? ConflictResponse(PowerActionInProgressMessage(pc))
            : Results.NoContent();
    }

    private static string PowerActionInProgressMessage(Pc pc) =>
        PcShutdownRules.ActionOf(pc.PowerAction) == PcShutdownRules.RestartAction
            ? "This PC is already restarting."
            : "This PC is already shutting down.";

    private static async Task<IResult> RequestPowerActionManyAsync(
        string action,
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
        var requestIds = (await db.Pcs
            .AsNoTracking()
            .Where(p => pcIds.Contains(p.Id))
            .Select(p => new { p.Id, p.LastSeenUtc, p.IpAddress })
            .ToListAsync(ct))
            .Where(p => IsOnline(p.LastSeenUtc, now) && !ServerPcRule.IsServerPc(p.IpAddress, machineAddresses))
            .Select(p => p.Id)
            .ToArray();
        var requested = await RequestPowerActionsAsync(db, requestIds, action, now, ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new AffectedPcsResponse(requested));
    }

    // Requests a shutdown or restart for each listed PC that isn't already pending. A request that went stale is replaced.
    // A PC shut down or restarted within the delivery window is skipped, so a fresh delivery isn't followed by a second request.
    // Replacing a request also forgets an older delivery, so the stored action always belongs to the stored request.
    // Returns how many PCs were newly requested.
    private static async Task<int> RequestPowerActionsAsync(AppDbContext db, int[] pcIds, string action, DateTimeOffset now, CancellationToken ct)
    {
        var expiredBefore = now - PcShutdownRules.RequestWindow;
        var candidates = await db.Pcs
            .AsNoTracking()
            .Where(p => pcIds.Contains(p.Id) && (p.ShutdownRequestedAtUtc == null || p.ShutdownRequestedAtUtc <= expiredBefore))
            .Select(p => new { p.Id, p.ShutdownRequestedAtUtc, p.ShutdownDeliveredAtUtc, p.PowerCountdownSeconds })
            .ToListAsync(ct);

        // Each delivery window depends on the countdown stored with that delivery, so it is checked for each PC here. The update
        // matches the values read, so a PC that a heartbeat changed meanwhile is left alone and not requested.
        var requested = 0;
        foreach (var pc in candidates.Where(p => !PcShutdownRules.IsDeliveryInWindow(p.ShutdownDeliveredAtUtc, p.PowerCountdownSeconds, now)))
        {
            requested += await db.Pcs
                .Where(p => p.Id == pc.Id
                    && p.ShutdownRequestedAtUtc == pc.ShutdownRequestedAtUtc
                    && p.ShutdownDeliveredAtUtc == pc.ShutdownDeliveredAtUtc)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)now)
                    .SetProperty(p => p.ShutdownDeliveredAtUtc, (DateTimeOffset?)null)
                    .SetProperty(p => p.PowerAction, action), ct);
        }

        return requested;
    }

    private static string ServerPcMessage(string action) =>
        action == PcShutdownRules.RestartAction
            ? "This PC runs the server. Restart it by hand."
            : "This PC runs the server. Shut it down by hand.";
}
