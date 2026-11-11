using System.Text.Json.Serialization;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Wallpapers;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Sent by the administrator. Ids names the PCs, as in PcIdsRequest. WallpaperId is the wallpaper to give them, or null to remove it.
// WallpaperId must be in the body, so a body that leaves it out is refused rather than read as a removal.
public record PcWallpaperRequest(int[]? Ids, [property: JsonRequired] int? WallpaperId);

public static partial class PcEndpoints
{
    private const string WallpaperNotFoundMessage = "Wallpaper not found.";

    // The PC applies the wallpaper on its next heartbeat, so an offline PC is updated too. Null removes the wallpaper.
    private static async Task<IResult> SetWallpaperAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        var request = await AuthEndpoints.ReadBodyAsync<PcWallpaperRequest>(http.Request);
        var pcIds = ValidPcIds(request?.Ids);
        if (request is null || pcIds is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var ct = http.RequestAborted;
        var wallpaperId = request.WallpaperId;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (wallpaperId is { } id && !await db.Wallpapers.AnyAsync(w => w.Id == id, ct))
        {
            return Results.Json(new ErrorResponse(WallpaperNotFoundMessage), statusCode: StatusCodes.Status404NotFound);
        }

        // The wallpapers these PCs had before. Once they are changed, any of them with no PC left is removed.
        var previousIds = (await db.Pcs
            .AsNoTracking()
            .Where(p => pcIds.Contains(p.Id))
            .Select(p => p.WallpaperId)
            .Distinct()
            .ToListAsync(ct))
            .OfType<int>()
            .ToArray();
        var affected = await db.Pcs
            .Where(p => pcIds.Contains(p.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.WallpaperId, wallpaperId), ct);
        await WallpaperCleanup.RemoveUnusedAsync(db, previousIds, ct);
        await WallpaperCleanup.RemoveStaleUnassignedAsync(db, clock.GetUtcNow(), ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(new AffectedPcsResponse(affected));
    }
}
