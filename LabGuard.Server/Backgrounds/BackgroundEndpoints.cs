using LabGuard.Server.Auth;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using LabGuard.Server.Pcs;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Backgrounds;

// Metadata of the image set for a screen. Sizes and hashes only, so the administrator's list never loads the image bytes.
public record BackgroundInfo(string ContentType, long SizeBytes, string Sha256, DateTimeOffset UpdatedAtUtc);
// Login and Lock are null when no image is set for that screen.
public record BackgroundsResponse(BackgroundInfo? Login, BackgroundInfo? Lock);

public static class BackgroundEndpoints
{
    private const string UnknownKindMessage = "Unknown screen background.";
    private const string NotSetMessage = "No background is set for this screen.";
    private const string InvalidImageMessage = "Choose a PNG or JPEG image.";
    private const string TooLargeMessage = "The image must be 5 MB or smaller.";
    private const string TooManyPixelsMessage = "The image must be 7680 × 4320 pixels (8K) or smaller.";
    // SQLite reports a duplicate text primary key with this code, not the unique-index code that ClientEndpoints.IsUniqueViolation checks.
    private const int SqlitePrimaryKeyViolationCode = 1555;
    private const int ReadChunkBytes = 80 * 1024;

    private sealed record StoredBackground(string Kind, string ContentType, long SizeBytes, string Sha256, DateTime UpdatedAtUtc);
    private sealed record BackgroundFile(string ContentType, byte[] Data, string Sha256);
    private sealed record ImageUpload(string Kind, string ContentType, byte[] Data, string Sha256, DateTime UpdatedAtUtc);

    public static IEndpointRouteBuilder MapBackgroundEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/backgrounds");
        // Like the session logs, these answers are never cached.
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.AddEndpointFilter<PcStorageErrorFilter>();
        group.MapGet("/", ListAsync);
        group.MapGet("/{kind}/image", ImageAsync);
        group.MapPut("/{kind}", SaveAsync);
        group.MapDelete("/{kind}", DeleteAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var stored = await db.ScreenBackgrounds
            .AsNoTracking()
            .Select(b => new StoredBackground(b.Kind, b.ContentType, b.Data.Length, b.Sha256, b.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        return Results.Ok(new BackgroundsResponse(
            InfoOf(stored, ScreenBackgroundKinds.Login),
            InfoOf(stored, ScreenBackgroundKinds.Lock)));
    }

    private static async Task<IResult> ImageAsync(string kind, AppDbContext db, HttpContext http)
    {
        if (!ScreenBackgroundKinds.IsKnown(kind))
        {
            return Error(UnknownKindMessage, StatusCodes.Status404NotFound);
        }

        if (await FindFileAsync(db, kind, http.RequestAborted) is not { } file)
        {
            return Error(NotSetMessage, StatusCodes.Status404NotFound);
        }

        return ImageResult(http, file);
    }

    // The body is the raw image, so the declared content type and the file signature are both checked before anything is stored.
    private static async Task<IResult> SaveAsync(string kind, HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (!ScreenBackgroundKinds.IsKnown(kind))
        {
            return Error(UnknownKindMessage, StatusCodes.Status404NotFound);
        }

        var mediaType = BackgroundImageRules.MediaTypeOf(http.Request.ContentType);
        if (!BackgroundImageRules.IsSupportedMediaType(mediaType))
        {
            return AuthEndpoints.BadRequest(InvalidImageMessage);
        }

        if (http.Request.ContentLength is { } declaredBytes && !BackgroundImageRules.IsWithinSizeLimit(declaredBytes))
        {
            return Error(TooLargeMessage, StatusCodes.Status413PayloadTooLarge);
        }

        var ct = http.RequestAborted;
        if (await ReadWithinLimitAsync(http.Request.Body, ct) is not { } data)
        {
            return Error(TooLargeMessage, StatusCodes.Status413PayloadTooLarge);
        }

        if (!BackgroundImageRules.IsValidImage(mediaType, data)
            || !BackgroundImageRules.TryReadPixelSize(mediaType, data, out var width, out var height))
        {
            return AuthEndpoints.BadRequest(InvalidImageMessage);
        }

        if (!BackgroundImageRules.IsWithinPixelLimit(width, height))
        {
            return AuthEndpoints.BadRequest(TooManyPixelsMessage);
        }

        var now = clock.GetUtcNow();
        var sha256 = BackgroundImageRules.Sha256Of(data);
        await StoreAsync(db, new ImageUpload(kind, mediaType, data, sha256, now.UtcDateTime), ct);

        return Results.Ok(new BackgroundInfo(mediaType, data.Length, sha256, now));
    }

    private static async Task<IResult> DeleteAsync(string kind, AppDbContext db, CancellationToken cancellationToken)
    {
        if (!ScreenBackgroundKinds.IsKnown(kind))
        {
            return Error(UnknownKindMessage, StatusCodes.Status404NotFound);
        }

        // Idempotent: removing a background that is not set is still a success.
        await db.ScreenBackgrounds.Where(b => b.Kind == kind).ExecuteDeleteAsync(cancellationToken);
        return Results.NoContent();
    }

    // The PC's download. It authenticates with its token, the same way its heartbeat does. The hash is the ETag, so a PC that
    // already has the current image sends If-None-Match and gets an empty 304 instead of the bytes again.
    internal static async Task<IResult> DownloadForPcAsync(string kind, HttpContext http, AppDbContext db)
    {
        if (ClientEndpoints.ReadBearerToken(http.Request) is not { } token)
        {
            return ClientEndpoints.NotRegistered();
        }

        var ct = http.RequestAborted;
        if (await ClientEndpoints.FindPcIdAsync(db, token, ct) is null)
        {
            return ClientEndpoints.NotRegistered();
        }

        if (!ScreenBackgroundKinds.IsKnown(kind))
        {
            return Error(UnknownKindMessage, StatusCodes.Status404NotFound);
        }

        if (await FindShaAsync(db, kind, ct) is not { } sha256)
        {
            return Error(NotSetMessage, StatusCodes.Status404NotFound);
        }

        var etag = $"\"{sha256}\"";
        http.Response.Headers["ETag"] = etag;
        http.Response.Headers["X-Background-Sha256"] = sha256;
        if (IfNoneMatchContains(http.Request.Headers.IfNoneMatch.ToString(), etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        // Re-read with the bytes. The image could have been deleted since the hash was read.
        if (await FindFileAsync(db, kind, ct) is not { } file)
        {
            return Error(NotSetMessage, StatusCodes.Status404NotFound);
        }

        return ImageResult(http, file);
    }

    // If-None-Match can hold several quoted tags, separated by commas.
    private static bool IfNoneMatchContains(string headerValue, string etag) =>
        headerValue.Split(',').Any(candidate => candidate.Trim() == etag);

    // The hash of each image that is set, for the heartbeat. Only the kind and hash columns are read, never the image bytes.
    // Null when neither is set, so the heartbeat leaves the field out.
    internal static async Task<HeartbeatBackgrounds?> HeartbeatHashesAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var hashes = await db.ScreenBackgrounds
            .AsNoTracking()
            .Select(b => new { b.Kind, b.Sha256 })
            .ToListAsync(cancellationToken);

        var login = hashes.FirstOrDefault(h => h.Kind == ScreenBackgroundKinds.Login)?.Sha256;
        var lockHash = hashes.FirstOrDefault(h => h.Kind == ScreenBackgroundKinds.Lock)?.Sha256;
        return login is null && lockHash is null ? null : new HeartbeatBackgrounds(login, lockHash);
    }

    private static Task<string?> FindShaAsync(AppDbContext db, string kind, CancellationToken cancellationToken) =>
        db.ScreenBackgrounds
            .AsNoTracking()
            .Where(b => b.Kind == kind)
            .Select(b => b.Sha256)
            .FirstOrDefaultAsync(cancellationToken);

    // Two first uploads for the same kind can both find no row and both insert. The loser's insert fails on the key, so it
    // starts over once: its read now finds the winner's row and it updates that row instead.
    private static async Task StoreAsync(AppDbContext db, ImageUpload upload, CancellationToken cancellationToken)
    {
        try
        {
            await UpsertAsync(db, upload, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsKindAlreadyStored(exception))
        {
            db.ChangeTracker.Clear();
            await UpsertAsync(db, upload, cancellationToken);
        }
    }

    private static async Task UpsertAsync(AppDbContext db, ImageUpload upload, CancellationToken cancellationToken)
    {
        var background = await db.ScreenBackgrounds.FirstOrDefaultAsync(b => b.Kind == upload.Kind, cancellationToken);
        if (background is null)
        {
            background = new ScreenBackground { Kind = upload.Kind };
            db.ScreenBackgrounds.Add(background);
        }

        background.ContentType = upload.ContentType;
        background.Data = upload.Data;
        background.Sha256 = upload.Sha256;
        background.UpdatedAtUtc = upload.UpdatedAtUtc;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsKindAlreadyStored(DbUpdateException exception) =>
        ClientEndpoints.IsUniqueViolation(exception)
        || exception.InnerException is SqliteException { SqliteExtendedErrorCode: SqlitePrimaryKeyViolationCode };

    private static Task<BackgroundFile?> FindFileAsync(AppDbContext db, string kind, CancellationToken cancellationToken) =>
        db.ScreenBackgrounds
            .AsNoTracking()
            .Where(b => b.Kind == kind)
            .Select(b => new BackgroundFile(b.ContentType, b.Data, b.Sha256))
            .FirstOrDefaultAsync(cancellationToken);

    // Reads the whole body, but stops as soon as it would pass the limit, so an oversized upload never fills memory.
    // Null when the body is over the limit.
    private static async Task<byte[]?> ReadWithinLimitAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[ReadChunkBytes];
        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > BackgroundImageRules.MaxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static BackgroundInfo? InfoOf(List<StoredBackground> stored, string kind) =>
        stored.FirstOrDefault(b => b.Kind == kind) is { } background
            ? new BackgroundInfo(
                background.ContentType,
                background.SizeBytes,
                background.Sha256,
                new DateTimeOffset(DateTime.SpecifyKind(background.UpdatedAtUtc, DateTimeKind.Utc)))
            : null;

    // The image is shown inline and never executed: no sniffing, and a CSP that blocks any script, style or embedded content.
    private static IResult ImageResult(HttpContext http, BackgroundFile file)
    {
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Content-Disposition"] = "inline";
        http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        return Results.Bytes(file.Data, file.ContentType);
    }

    private static IResult Error(string message, int statusCode) =>
        Results.Json(new ErrorResponse(message), statusCode: statusCode);
}
