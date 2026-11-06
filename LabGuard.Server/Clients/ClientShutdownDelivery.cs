using LabGuard.Server.Data;
using LabGuard.Server.Network;
using LabGuard.Server.Pcs;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Clients;

// Hands a requested shutdown or restart to the PC on its heartbeat. Only the update that clears a pending request reports it,
// so a second heartbeat in a row, or a PC that restarts, never gets the same action again.
// A request is never handed to a PC that was offline before this heartbeat, or to the PC this server runs on.
public static partial class ClientEndpoints
{
    // Returns the action handed to the PC on this heartbeat, or null when this heartbeat carries none.
    private static async Task<string?> DeliverPowerActionAsync(
        AppDbContext db,
        string tokenHash,
        string? address,
        bool wasOnline,
        ILocalAddressSource localAddresses,
        DateTimeOffset? requestedAt,
        string? storedAction,
        DateTimeOffset? deliveredAt,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // A PC that was offline before this heartbeat has only just started. Whatever it was asked to do is forgotten at
        // once, so it isn't shown as still going down, and a pending request is dropped rather than handed over.
        if (!wasOnline)
        {
            await db.Pcs
                .Where(p => p.TokenHash == tokenHash && (p.ShutdownRequestedAtUtc != null || p.ShutdownDeliveredAtUtc != null))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)null)
                    .SetProperty(p => p.ShutdownDeliveredAtUtc, (DateTimeOffset?)null), ct);
            return null;
        }

        // A delivery more than the window old is forgotten: the PC is no longer shutting down or restarting.
        var deliveredBefore = now - PcShutdownRules.DeliveryWindow;
        if (deliveredAt is { } lastDelivered && lastDelivered < deliveredBefore)
        {
            await db.Pcs
                .Where(p => p.TokenHash == tokenHash && p.ShutdownDeliveredAtUtc < deliveredBefore)
                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ShutdownDeliveredAtUtc, (DateTimeOffset?)null), ct);
        }

        if (requestedAt is not { } requested)
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

        // The update matches this exact request, so a concurrent heartbeat that already took it gets nothing, and a request
        // replaced after it was read is left for a later heartbeat, which reads the action that belongs to it.
        var changed = await db.Pcs
            .Where(p => p.TokenHash == tokenHash && p.ShutdownRequestedAtUtc == requested)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)null)
                .SetProperty(p => p.ShutdownDeliveredAtUtc, (DateTimeOffset?)now), ct);
        return changed > 0 ? PcShutdownRules.ActionOf(storedAction) : null;
    }

    // Drops a pending request without delivering it.
    private static Task<int> ClearRequestAsync(AppDbContext db, string tokenHash, CancellationToken ct) =>
        db.Pcs
            .Where(p => p.TokenHash == tokenHash && p.ShutdownRequestedAtUtc != null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)null), ct);
}
