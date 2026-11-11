using System.Text.Json.Serialization;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Sent by the administrator. Ids names the PCs, as in PcIdsRequest. KeepAwake turns the setting on or off.
// KeepAwake must be in the body and not null, so a body that leaves it out is refused rather than read as off.
public record PcKeepAwakeRequest(int[]? Ids, [property: JsonRequired] bool? KeepAwake);

public static partial class PcEndpoints
{
    // Turning it on changes only PCs online right now; offline and unknown PCs are skipped and left out of the count.
    // Turning it off changes every named PC, offline ones too: a PC held at the sign-in screen has no client heartbeat,
    // so it shows offline, yet its service still picks up the change from its next report.
    private static async Task<IResult> SetKeepAwakeAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        var request = await AuthEndpoints.ReadBodyAsync<PcKeepAwakeRequest>(http.Request);
        var pcIds = ValidPcIds(request?.Ids);
        if (request?.KeepAwake is not { } keepAwake || pcIds is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var pcs = db.Pcs.Where(p => pcIds.Contains(p.Id));
        if (keepAwake)
        {
            // The same rule as IsOnline: seen within the window, and never in the future.
            var now = clock.GetUtcNow();
            var offlineSince = now - OnlineWindow;
            pcs = pcs.Where(p => p.LastSeenUtc > offlineSince && p.LastSeenUtc <= now);
        }

        var affected = await pcs
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.KeepAwake, keepAwake), http.RequestAborted);

        return Results.Ok(new AffectedPcsResponse(affected));
    }
}
