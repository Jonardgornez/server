using LabGuard.Server.Data;
using LabGuard.Server.Network;
using LabGuard.Server.Pcs;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Clients;

// Hands a requested shutdown to the PC on its heartbeat. Only the update that clears a pending request reports it,
// so a second heartbeat in a row, or a PC that restarts, never gets the same shutdown again.
// A request is never handed to a PC that was offline before this heartbeat, or to the PC this server runs on.
public static partial class ClientEndpoints
{
    // Returns the seconds to shut down in, or null when this heartbeat carries no shutdown.
    private static async Task<int?> DeliverShutdownAsync(
        AppDbContext db,
        string tokenHash,
        string? address,
        bool wasOnline,
        ILocalAddressSource localAddresses,
        DateTimeOffset? requestedAt,
        DateTimeOffset? deliveredAt,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // A delivery more than the window old is forgotten: the PC has come back, so it is no longer shutting down.
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

        // A PC that was offline before this heartbeat has only just started, so it must not be shut down.
        if (!wasOnline)
        {
            await ClearRequestAsync(db, tokenHash, ct);
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

        // The condition is checked again in the update, so a concurrent heartbeat that already took the request gets nothing.
        var changed = await db.Pcs
            .Where(p => p.TokenHash == tokenHash && p.ShutdownRequestedAtUtc > requestedBefore)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)null)
                .SetProperty(p => p.ShutdownDeliveredAtUtc, (DateTimeOffset?)now), ct);
        return changed > 0 ? PcShutdownRules.DeliveredShutdownSeconds : null;
    }

    // Drops a pending request without delivering it.
    private static Task<int> ClearRequestAsync(AppDbContext db, string tokenHash, CancellationToken ct) =>
        db.Pcs
            .Where(p => p.TokenHash == tokenHash && p.ShutdownRequestedAtUtc != null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)null), ct);
}
