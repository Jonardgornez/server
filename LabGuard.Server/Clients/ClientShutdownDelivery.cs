using LabGuard.Server.Data;
using LabGuard.Server.Pcs;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Clients;

// Hands a requested shutdown to the PC on its heartbeat. Only the update that clears a pending request reports it,
// so a second heartbeat in a row, or a PC that restarts, never gets the same shutdown again.
public static partial class ClientEndpoints
{
    // Returns the seconds to shut down in, or null when this heartbeat carries no shutdown.
    private static async Task<int?> DeliverShutdownAsync(
        AppDbContext db,
        string tokenHash,
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

        // The condition is checked again in the update, so a concurrent heartbeat that already took the request gets nothing.
        var changed = await db.Pcs
            .Where(p => p.TokenHash == tokenHash && p.ShutdownRequestedAtUtc > requestedBefore)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.ShutdownRequestedAtUtc, (DateTimeOffset?)null)
                .SetProperty(p => p.ShutdownDeliveredAtUtc, (DateTimeOffset?)now), ct);
        return changed > 0 ? PcShutdownRules.DeliveredShutdownSeconds : null;
    }
}
