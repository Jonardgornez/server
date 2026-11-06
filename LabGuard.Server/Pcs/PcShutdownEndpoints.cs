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

        // A cancel waiting for the PC to acknowledge it holds the delivery, so nothing is requested until that is done.
        if (PcShutdownRules.StateOf(pc, isOnline: true, now) == PcShutdownRules.CancellingState)
        {
            return ConflictResponse(CancellingMessage(pc));
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
    // Replacing a request also forgets an older delivery and any cancel of it, so the stored action always belongs to the stored request.
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
                    .SetProperty(p => p.PowerCancelRequestedAtUtc, (DateTimeOffset?)null)
                    .SetProperty(p => p.PowerAction, action), ct);
        }

        return requested;
    }

    private static async Task<IResult> CancelPowerAsync(int id, HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (await AuthEndpoints.ReadBodyAsync<PcAdminActionRequest>(http.Request) is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        // Cancelling needs no address check: the PC that runs the server never gets a delivery, so there is nothing to cancel there.
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SessionLifecycle.EndExpiredAsync(db, now, ct);
        var outcome = await CancelPowerForPcAsync(db, id, now, ct);
        await transaction.CommitAsync(ct);

        return outcome switch
        {
            PowerCancelOutcome.NotFound => PcNotFound(),
            PowerCancelOutcome.Offline => ConflictResponse(CancelOfflineMessage),
            PowerCancelOutcome.NothingToCancel => ConflictResponse(NothingToCancelMessage),
            _ => Results.NoContent(),
        };
    }

    private static async Task<IResult> CancelPowerManyAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        var pcIds = await ReadPcIdsAsync(http.Request);
        if (pcIds is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SessionLifecycle.EndExpiredAsync(db, now, ct);
        var cancelled = 0;
        foreach (var pcId in pcIds)
        {
            if (await CancelPowerForPcAsync(db, pcId, now, ct) == PowerCancelOutcome.Cancelled)
            {
                cancelled++;
            }
        }

        await transaction.CommitAsync(ct);
        return Results.Ok(new AffectedPcsResponse(cancelled));
    }

    private enum PowerCancelOutcome
    {
        NotFound,
        Offline,
        NothingToCancel,
        AlreadyCancelling,
        Cancelled,
    }

    // Cancels the shutdown or restart of one PC, if it has one. A pending request is dropped, since nothing was delivered.
    // A delivery still in its window is cancelled on an online PC, and kept until the PC acknowledges the cancel, so a PC that
    // goes down anyway is still shown as shutting down or rebooting.
    private static async Task<PowerCancelOutcome> CancelPowerForPcAsync(AppDbContext db, int pcId, DateTimeOffset now, CancellationToken ct)
    {
        // The pending request is checked first. A heartbeat that delivers the request between these two checks is caught by
        // the delivery check below, which reads the PC again rather than assuming the request is still pending.
        var dropped = await db.Pcs
            .Where(p => p.Id == pcId && p.ShutdownRequestedAtUtc > now - PcShutdownRules.RequestWindow)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)null), ct);
        if (dropped > 0)
        {
            return PowerCancelOutcome.Cancelled;
        }

        var pc = await db.Pcs
            .AsNoTracking()
            .Where(p => p.Id == pcId)
            .Select(p => new { p.LastSeenUtc, p.ShutdownDeliveredAtUtc, p.PowerCountdownSeconds, p.PowerCancelRequestedAtUtc })
            .FirstOrDefaultAsync(ct);
        if (pc is null)
        {
            return PowerCancelOutcome.NotFound;
        }

        if (!PcShutdownRules.IsDeliveryInWindow(pc.ShutdownDeliveredAtUtc, pc.PowerCountdownSeconds, now))
        {
            return PowerCancelOutcome.NothingToCancel;
        }

        // An offline PC never gets the cancel, so its delivery is left as it is.
        if (!IsOnline(pc.LastSeenUtc, now))
        {
            return PowerCancelOutcome.Offline;
        }

        if (pc.PowerCancelRequestedAtUtc is not null)
        {
            return PowerCancelOutcome.AlreadyCancelling;
        }

        // Matches the delivery that was read, so a delivery forgotten or acknowledged meanwhile is not cancelled.
        var recorded = await db.Pcs
            .Where(p => p.Id == pcId && p.ShutdownDeliveredAtUtc == pc.ShutdownDeliveredAtUtc)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.PowerCancelRequestedAtUtc, (DateTimeOffset?)now), ct);
        return recorded > 0 ? PowerCancelOutcome.Cancelled : PowerCancelOutcome.NothingToCancel;
    }

    private static string CancellingMessage(Pc pc) =>
        PcShutdownRules.ActionOf(pc.PowerAction) == PcShutdownRules.RestartAction
            ? "This PC is cancelling its restart. Try again once the cancel is done."
            : "This PC is cancelling its shutdown. Try again once the cancel is done.";

    private const string NothingToCancelMessage = "This PC has no shutdown or restart to cancel.";
    private const string CancelOfflineMessage = "This PC is offline. Its shutdown or restart can't be cancelled.";

    private static string ServerPcMessage(string action) =>
        action == PcShutdownRules.RestartAction
            ? "This PC runs the server. Restart it by hand."
            : "This PC runs the server. Shut it down by hand.";
}
