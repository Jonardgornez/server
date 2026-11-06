using LabGuard.Server.Data;
using LabGuard.Server.Network;
using LabGuard.Server.Pcs;
using LabGuard.Server.Settings;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Clients;

// Hands a requested shutdown or restart to the PC on its heartbeat, and tells it about an administrator's cancel of one.
// Only the update that clears a pending request reports it, so a second heartbeat in a row, or a PC that restarts, never gets
// the same action again. A request is never handed to a PC that was offline before this heartbeat, or to the PC this server runs on.
// A cancel is cleared when the PC acknowledges it, or with the delivery it cancels. Nothing the PC sends can start or extend one.
public static partial class ClientEndpoints
{
    // The stored shutdown or restart as a heartbeat reads it, before the heartbeat changes anything.
    private sealed record PowerSnapshot(
        DateTimeOffset? RequestedAtUtc,
        DateTimeOffset? DeliveredAtUtc,
        string? PowerAction,
        int? CountdownSeconds,
        DateTimeOffset? CancelRequestedAtUtc);

    // The action handed to the PC on a heartbeat, with the countdown stored for it.
    private sealed record PowerDelivery(string Action, int CountdownSeconds);

    // What a heartbeat tells the PC about its shutdown or restart: the delivery made on this heartbeat, or null, and whether
    // a cancel is still recorded for the PC to acknowledge.
    private sealed record PowerHandOff(PowerDelivery? Delivery, bool IsCancelled);

    private static async Task<PowerHandOff> HandOffPowerAsync(
        AppDbContext db,
        string tokenHash,
        string? address,
        bool wasOnline,
        ILocalAddressSource localAddresses,
        PowerSnapshot power,
        bool? cancelHandled,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // A PC that was offline before this heartbeat has only just started. Whatever it was asked to do is forgotten at
        // once, so it isn't shown as still going down, and a pending request or a cancel is dropped rather than handed over.
        if (!wasOnline)
        {
            await db.Pcs
                .Where(p => p.TokenHash == tokenHash
                    && (p.ShutdownRequestedAtUtc != null || p.ShutdownDeliveredAtUtc != null || p.PowerCancelRequestedAtUtc != null))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)null)
                    .SetProperty(p => p.ShutdownDeliveredAtUtc, (DateTimeOffset?)null)
                    .SetProperty(p => p.PowerCancelRequestedAtUtc, (DateTimeOffset?)null), ct);
            return new PowerHandOff(null, IsCancelled: false);
        }

        // A delivery more than its own window old is forgotten: the PC is no longer shutting down or restarting, so a cancel goes with it.
        var deliveredBefore = now - PcShutdownRules.DeliveryWindowOf(power.CountdownSeconds);
        if (power.DeliveredAtUtc is { } lastDelivered && lastDelivered < deliveredBefore)
        {
            await db.Pcs
                .Where(p => p.TokenHash == tokenHash && p.ShutdownDeliveredAtUtc < deliveredBefore)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.ShutdownDeliveredAtUtc, (DateTimeOffset?)null)
                    .SetProperty(p => p.PowerCancelRequestedAtUtc, (DateTimeOffset?)null), ct);
            power = power with { DeliveredAtUtc = null, CancelRequestedAtUtc = null };
        }

        // The PC has acted on the cancel, so the cancel and its delivery are cleared. The update matches the cancel that was read,
        // so a cancel recorded meanwhile is left for the next heartbeat to report. An acknowledgement with no cancel does nothing.
        if (cancelHandled == true && power.CancelRequestedAtUtc is { } cancelRecorded)
        {
            await db.Pcs
                .Where(p => p.TokenHash == tokenHash && p.PowerCancelRequestedAtUtc == cancelRecorded)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.ShutdownDeliveredAtUtc, (DateTimeOffset?)null)
                    .SetProperty(p => p.PowerCancelRequestedAtUtc, (DateTimeOffset?)null), ct);
            power = power with { DeliveredAtUtc = null, CancelRequestedAtUtc = null };
        }

        var delivery = await DeliverRequestAsync(db, tokenHash, address, localAddresses, power, now, ct);
        return new PowerHandOff(delivery, IsCancelled: power.CancelRequestedAtUtc is not null);
    }

    // Returns the pending request handed to the PC on this heartbeat with its countdown, or null when this heartbeat hands over none.
    private static async Task<PowerDelivery?> DeliverRequestAsync(
        AppDbContext db,
        string tokenHash,
        string? address,
        ILocalAddressSource localAddresses,
        PowerSnapshot power,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (power.RequestedAtUtc is not { } requested)
        {
            return null;
        }

        var requestedBefore = now - PcShutdownRules.RequestWindow;
        if (requested <= requestedBefore)
        {
            // Too old to deliver. It is dropped without being sent.
            await db.Pcs
                .Where(p => p.TokenHash == tokenHash && p.ShutdownRequestedAtUtc <= requestedBefore)
                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)null), ct);
            return null;
        }

        // Only read here, while a request is pending, so an ordinary heartbeat never touches the network adapters.
        // Fails closed: without a live address or the machine's addresses, this PC can't be shown not to be the server.
        if (address is null || localAddresses.ReadOrNull() is not { } machineAddresses)
        {
            return null;
        }

        // The live connection address decides, not the stored one.
        if (ServerPcRule.IsServerPc(address, machineAddresses))
        {
            await ClearRequestAsync(db, tokenHash, ct);
            return null;
        }

        // The countdown in force now is stored with this delivery, so a later change to the setting leaves it alone.
        var countdown = (await ServerSettingsStore.GetAsync(db, ct)).PowerCountdownSeconds;

        // The update matches this exact request, so a concurrent heartbeat that already took it gets nothing, and a request
        // replaced after it was read is left for a later heartbeat, which reads the action that belongs to it.
        var changed = await db.Pcs
            .Where(p => p.TokenHash == tokenHash && p.ShutdownRequestedAtUtc == requested)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)null)
                .SetProperty(p => p.ShutdownDeliveredAtUtc, (DateTimeOffset?)now)
                .SetProperty(p => p.PowerCountdownSeconds, (int?)countdown), ct);
        return changed > 0 ? new PowerDelivery(PcShutdownRules.ActionOf(power.PowerAction), countdown) : null;
    }

    // Drops a pending request without delivering it.
    private static Task<int> ClearRequestAsync(AppDbContext db, string tokenHash, CancellationToken ct) =>
        db.Pcs
            .Where(p => p.TokenHash == tokenHash && p.ShutdownRequestedAtUtc != null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)null), ct);
}
