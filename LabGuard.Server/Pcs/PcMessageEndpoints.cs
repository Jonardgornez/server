using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Sent by the admin. Text is plain text, and the PCs are named by id.
public record PcMessageRequest(int[]? PcIds, string? Text);
// Status is one of the PcMessageRules status strings. PcName is null for an id that matches no PC.
public record PcMessageResult(int PcId, string? PcName, string Status);
public record PcMessagesResponse(PcMessageResult[] Results);

public static partial class PcEndpoints
{
    private static readonly TimeSpan MessagePollInterval = TimeSpan.FromMilliseconds(150);

    private static async Task<IResult> SendMessageAsync(HttpContext http, AppDbContext db, TimeProvider clock, PcMessageSends sends)
    {
        var request = await AuthEndpoints.ReadBodyAsync<PcMessageRequest>(http.Request);
        if (request is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        if (!PcMessageRules.TryCheckText(request.Text, out var text, out var textError))
        {
            return AuthEndpoints.BadRequest(textError);
        }

        var pcIds = request.PcIds ?? [];
        if (PcMessageRules.RecipientsError(pcIds) is { } recipientsError)
        {
            return AuthEndpoints.BadRequest(recipientsError);
        }

        // Refused rather than queued when both slots are taken.
        if (!sends.InFlight.Wait(0))
        {
            return Results.Json(
                new ErrorResponse("A message is already being sent. Try again in a moment."),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        try
        {
            return await SendAcceptedMessageAsync(http, db, clock, sends, pcIds, text);
        }
        finally
        {
            sends.InFlight.Release();
        }
    }

    private static async Task<IResult> SendAcceptedMessageAsync(
        HttpContext http,
        AppDbContext db,
        TimeProvider clock,
        PcMessageSends sends,
        int[] pcIds,
        string text)
    {
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        var (refused, stored) = await StoreMessagesAsync(db, sends, pcIds, text, http.User.Identity?.Name ?? "", now, ct);

        // Every stored message is resolved when it is delivered, or when its delivery window closes. The window is read from the clock.
        var messageIds = stored.Select(m => m.Id).ToArray();
        var deadline = now + PcMessageRules.DeliveryWindow;
        await WaitForDeliveryAsync(db, messageIds, deadline, clock, ct);

        var deliveredPcIds = (await db.PcMessages
            .AsNoTracking()
            .Where(m => messageIds.Contains(m.Id) && m.DeliveredUtc != null)
            .Select(m => m.PcId)
            .ToListAsync(ct))
            .ToHashSet();

        // Results follow the order the administrator listed the PCs in.
        var results = pcIds.Select(pcId => refused.TryGetValue(pcId, out var outcome)
                ? outcome
                : new PcMessageResult(
                    pcId,
                    stored.First(m => m.PcId == pcId).PcName,
                    deliveredPcIds.Contains(pcId) ? PcMessageRules.DeliveredStatus : PcMessageRules.FailedStatus))
            .ToArray();
        return Results.Ok(new PcMessagesResponse(results));
    }

    // Checks each listed PC and stores a message for each one that is online and has room. Returns the outcomes that were
    // decided at once (offline, not found or busy), keyed by PC id, and the messages stored.
    private static async Task<(Dictionary<int, PcMessageResult> Refused, List<PcMessage> Stored)> StoreMessagesAsync(
        AppDbContext db,
        PcMessageSends sends,
        int[] pcIds,
        string text,
        string adminUserName,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await sends.Store.WaitAsync(ct);
        try
        {
            var pcs = await db.Pcs
                .AsNoTracking()
                .Where(p => pcIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.LastSeenUtc })
                .ToDictionaryAsync(p => p.Id, ct);

            // Only messages inside the delivery window count. A message dated in the future is not fresh, so it doesn't count either.
            var undeliveredByPc = (await db.PcMessages
                .AsNoTracking()
                .Where(m => pcIds.Contains(m.PcId)
                    && m.DeliveredUtc == null
                    && m.CreatedUtc > now - PcMessageRules.DeliveryWindow
                    && m.CreatedUtc <= now)
                .Select(m => m.PcId)
                .ToListAsync(ct))
                .CountBy(pcId => pcId)
                .ToDictionary(pair => pair.Key, pair => pair.Value);

            var refused = new Dictionary<int, PcMessageResult>();
            var stored = new List<PcMessage>();
            foreach (var pcId in pcIds)
            {
                if (!pcs.TryGetValue(pcId, out var pc))
                {
                    refused[pcId] = new PcMessageResult(pcId, null, PcMessageRules.NotFoundStatus);
                }
                else if (!IsOnline(pc.LastSeenUtc, now))
                {
                    refused[pcId] = new PcMessageResult(pcId, pc.Name, PcMessageRules.OfflineStatus);
                }
                else if (undeliveredByPc.GetValueOrDefault(pcId) >= PcMessageRules.MaxUndeliveredPerPc)
                {
                    refused[pcId] = new PcMessageResult(pcId, pc.Name, PcMessageRules.BusyStatus);
                }
                else
                {
                    stored.Add(new PcMessage
                    {
                        PcId = pcId,
                        PcName = pc.Name,
                        AdminUserName = adminUserName,
                        Text = text,
                        CreatedUtc = now,
                    });
                }
            }

            db.PcMessages.AddRange(stored);
            await db.SaveChangesAsync(ct);
            return (refused, stored);
        }
        finally
        {
            sends.Store.Release();
        }
    }

    // Polls until every message is delivered or the deadline has passed. The poll pace is real time; the deadline is read from the clock.
    private static async Task WaitForDeliveryAsync(
        AppDbContext db,
        long[] messageIds,
        DateTimeOffset deadline,
        TimeProvider clock,
        CancellationToken ct)
    {
        while (messageIds.Length > 0)
        {
            var remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            var undelivered = await db.PcMessages
                .AsNoTracking()
                .CountAsync(m => messageIds.Contains(m.Id) && m.DeliveredUtc == null, ct);
            if (undelivered == 0)
            {
                return;
            }

            await Task.Delay(remaining < MessagePollInterval ? remaining : MessagePollInterval, ct);
        }
    }
}
