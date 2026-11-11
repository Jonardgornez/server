using LabGuard.Server.Auth;
using LabGuard.Server.Backgrounds;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using LabGuard.Server.Pcs;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Wallpapers;

// Sent back after an upload. SizeBytes is the size of the stored image. Uploading the same bytes again returns the stored row.
public record WallpaperUploadResponse(int Id, string Sha256, string ContentType, long SizeBytes);
// The wallpaper a PC is given, as the PC list shows it. The list leaves it null for a PC with none.
public record WallpaperRef(int Id, string Sha256);

// The desktop wallpapers the administrator gives PCs. Each distinct image is stored once, and PCs refer to it by id.
public static class WallpaperEndpoints
{
    private const string NotFoundMessage = "Wallpaper not found.";
    private const string NotSetMessage = "No wallpaper is set for this PC.";

    private sealed record WallpaperSummary(int Id, string ContentType, string Sha256, long SizeBytes);
    private sealed record WallpaperFile(string ContentType, byte[] Data);

    public static IEndpointRouteBuilder MapWallpaperEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/wallpapers");
        // Like the backgrounds, these answers are never cached.
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/", UploadAsync);
        group.MapGet("/{id:int}/image", ImageAsync);
        return app;
    }

    // The body is the raw image, checked the same way as a background before anything is stored.
    private static async Task<IResult> UploadAsync(HttpContext http, AppDbContext db, TimeProvider clock) =>
        await ImageUploadReader.ReadAsync(http.Request, async image =>
        {
            var stored = await StoreAsync(db, image, clock.GetUtcNow(), http.RequestAborted);
            return Results.Ok(new WallpaperUploadResponse(stored.Id, stored.Sha256, stored.ContentType, stored.SizeBytes));
        });

    private static async Task<IResult> ImageAsync(int id, HttpContext http, AppDbContext db)
    {
        var wallpaper = await db.Wallpapers
            .AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => new WallpaperFile(w.ContentType, w.Data))
            .FirstOrDefaultAsync(http.RequestAborted);
        return wallpaper is null
            ? Error(NotFoundMessage, StatusCodes.Status404NotFound)
            : BackgroundEndpoints.ImageResult(http, wallpaper.ContentType, wallpaper.Data);
    }

    // The PC's own wallpaper. The PC is identified by its token, as the heartbeat identifies it, and the image is the one
    // assigned to that PC. No id comes from the request, so a PC can't ask for another PC's wallpaper.
    internal static async Task<IResult> DownloadForPcAsync(HttpContext http, AppDbContext db)
    {
        if (ClientEndpoints.ReadBearerToken(http.Request) is not { } token)
        {
            return ClientEndpoints.NotRegistered();
        }

        var ct = http.RequestAborted;
        if (await ClientEndpoints.FindPcIdAsync(db, token, ct) is not { } pcId)
        {
            return ClientEndpoints.NotRegistered();
        }

        var assigned = await db.Pcs
            .AsNoTracking()
            .Where(p => p.Id == pcId)
            .Join(db.Wallpapers, p => p.WallpaperId, w => w.Id, (p, w) => new WallpaperFile(w.ContentType, w.Data))
            .FirstOrDefaultAsync(ct);
        if (assigned is null)
        {
            return Error(NotSetMessage, StatusCodes.Status404NotFound);
        }

        // The image changes when the administrator changes the PC's wallpaper, so the client must not keep an old copy.
        http.Response.Headers.CacheControl = "no-store";
        return BackgroundEndpoints.ImageResult(http, assigned.ContentType, assigned.Data);
    }

    // Stores the image unless the same bytes are already stored. Uploads that no PC was given in time are removed first.
    private static async Task<WallpaperSummary> StoreAsync(AppDbContext db, UploadedImage image, DateTimeOffset now, CancellationToken ct)
    {
        await WallpaperCleanup.RemoveStaleUnassignedAsync(db, now, ct);
        if (await FindBySha256Async(db, image.Sha256, ct) is { } existing)
        {
            return existing;
        }

        var wallpaper = new Wallpaper
        {
            ContentType = image.ContentType,
            Data = image.Data,
            Sha256 = image.Sha256,
            CreatedAtUtc = now,
        };
        db.Wallpapers.Add(wallpaper);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (ClientEndpoints.IsUniqueViolation(exception))
        {
            // The same bytes were stored by a concurrent upload after this one looked, so that row is used instead.
            db.ChangeTracker.Clear();
            return await FindBySha256Async(db, image.Sha256, ct)
                ?? throw new InvalidOperationException("The wallpaper reported as stored by a concurrent upload can't be read.");
        }

        return new WallpaperSummary(wallpaper.Id, wallpaper.ContentType, wallpaper.Sha256, wallpaper.Data.Length);
    }

    private static Task<WallpaperSummary?> FindBySha256Async(AppDbContext db, string sha256, CancellationToken ct) =>
        db.Wallpapers
            .AsNoTracking()
            .Where(w => w.Sha256 == sha256)
            .Select(w => new WallpaperSummary(w.Id, w.ContentType, w.Sha256, w.Data.Length))
            .FirstOrDefaultAsync(ct);

    private static IResult Error(string message, int statusCode) =>
        Results.Json(new ErrorResponse(message), statusCode: statusCode);
}
