using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Pcs;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Clients;

// A message as a heartbeat hands it to the PC.
public record PcMessageDelivery(long Id, string Text, DateTimeOffset SentUtc);

// Hands the administrator's messages to the PC on its heartbeat, and records the PC's acknowledgement of each one.
// A message is handed over once, oldest first, at most five per heartbeat, and only while it is still in its delivery window.
// Only the PC a message was sent to can acknowledge it.
public static partial class ClientEndpoints
{
    // Hands over this PC's messages that are still in their delivery window. Each update matches a message not yet handed over,
    // so two heartbeats running at the same time can't hand out the same message twice.
    private static async Task<PcMessageDelivery[]> HandOverMessagesAsync(AppDbContext db, int pcId, DateTimeOffset now, CancellationToken ct)
    {
        var candidates = await db.PcMessages
            .AsNoTracking()
            .Where(m => m.PcId == pcId
                && m.HandedOverUtc == null
                && m.CreatedUtc > now - PcMessageRules.DeliveryWindow
                && m.CreatedUtc <= now)
            .OrderBy(m => m.CreatedUtc)
            .ThenBy(m => m.Id)
            .Take(PcMessageRules.MaxHandOverPerHeartbeat)
            .Select(m => new { m.Id, m.Text, m.CreatedUtc })
            .ToListAsync(ct);

        var handedOver = new List<PcMessageDelivery>();
        foreach (var candidate in candidates)
        {
            var changed = await db.PcMessages
                .Where(m => m.Id == candidate.Id && m.HandedOverUtc == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.HandedOverUtc, (DateTimeOffset?)now), ct);
            if (changed > 0)
            {
                handedOver.Add(new PcMessageDelivery(candidate.Id, candidate.Text, candidate.CreatedUtc));
            }
        }

        return handedOver.ToArray();
    }

    // The PC has put the message on screen. Acknowledging it again does nothing more and still succeeds.
    private static async Task<IResult> AckMessageAsync(long id, HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (ReadBearerToken(http.Request) is not { } token)
        {
            return NotRegistered();
        }

        var ct = http.RequestAborted;
        if (await FindPcIdAsync(db, token, ct) is not { } pcId)
        {
            return NotRegistered();
        }

        var now = clock.GetUtcNow();

        // Another PC's message is looked up as missing, so a PC can't tell which messages exist.
        var message = await db.PcMessages
            .AsNoTracking()
            .Where(m => m.Id == id && m.PcId == pcId)
            .Select(m => new { m.CreatedUtc, m.HandedOverUtc, m.DeliveredUtc })
            .FirstOrDefaultAsync(ct);
        if (message is null || message.HandedOverUtc is null || !PcMessageRules.IsInAckWindow(message.CreatedUtc, now))
        {
            return MessageNotFound();
        }

        // Only an acknowledgement inside the delivery window counts as delivered. A later one is accepted, but the message stays failed.
        if (message.DeliveredUtc is null && PcMessageRules.IsInDeliveryWindow(message.CreatedUtc, now))
        {
            await db.PcMessages
                .Where(m => m.Id == id && m.PcId == pcId && m.DeliveredUtc == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.DeliveredUtc, (DateTimeOffset?)now), ct);
        }

        return Results.NoContent();
    }

    private static IResult MessageNotFound() =>
        Results.Json(new ErrorResponse("Message not found."), statusCode: StatusCodes.Status404NotFound);
}
