using System.Text.Json.Serialization;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Sent by the administrator. Ids names the PCs, as in PcIdsRequest. Disabled hides or shows Windows "Switch user" on them.
// Disabled must be in the body and not null, so a body that leaves it out is refused rather than read as allowed.
public record PcFastUserSwitchingRequest(int[]? Ids, [property: JsonRequired] bool? Disabled);

public static partial class PcEndpoints
{
    // Changes every named PC, offline ones too: the PC's service applies it from its next report, also at the sign-in screen.
    // Unknown ids are left out of the count.
    private static async Task<IResult> SetFastUserSwitchingAsync(HttpContext http, AppDbContext db)
    {
        var request = await AuthEndpoints.ReadBodyAsync<PcFastUserSwitchingRequest>(http.Request);
        var pcIds = ValidPcIds(request?.Ids);
        if (request?.Disabled is not { } disabled || pcIds is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var affected = await db.Pcs
            .Where(p => pcIds.Contains(p.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.FastUserSwitchingDisabled, disabled), http.RequestAborted);

        return Results.Ok(new AffectedPcsResponse(affected));
    }
}
