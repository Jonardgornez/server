using System.Security.Cryptography;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Pcs;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.SharedFiles;

// Sent back after an upload. Uploading the same bytes again returns the stored row, under the new name.
public record SharedFileUploadResponse(int Id, string Name, long SizeBytes, string Sha256);

// The files the administrator uploads to send to PCs. Each upload is the raw file, streamed to disk while it is hashed, so
// a file of any size never sits in memory. Each distinct file is stored once, under its hash.
public static class SharedFileEndpoints
{
    public const string FileNameHeader = "X-File-Name";
    public const string NoDiskSpaceMessage = "The server does not have enough free disk space for this file.";
    public const string UploadIncompleteMessage = "The upload didn't finish. Try again.";
    private const string OctetStream = "application/octet-stream";
    // An encoded 200-character name is at most 200 × 4 bytes × 3 characters. Anything longer can't decode to a valid name.
    private const int MaxEncodedNameLength = SharedFileRules.MaxFileNameLength * 12;
    private const int ReadChunkBytes = 80 * 1024;
    // How often, in written bytes, the free space is checked again while an upload streams in.
    private const long DiskCheckIntervalBytes = 16L * 1024 * 1024;

    private sealed record ReceivedFile(string Sha256, long SizeBytes);

    public static IServiceCollection AddSharedFiles(this IServiceCollection services)
    {
        services.AddSingleton<IDiskSpace, DriveDiskSpace>();
        services.AddSingleton(sp => new SharedFileStorage(
            SharedFileStorage.ResolveFolder(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>()),
            sp.GetRequiredService<IDiskSpace>(),
            sp.GetRequiredService<ILogger<SharedFileStorage>>()));
        services.AddHostedService<SharedFileCleanupService>();
        return services;
    }

    public static IEndpointRouteBuilder MapSharedFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/shared-files");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/", UploadAsync);
        return app;
    }

    private static async Task<IResult> UploadAsync(HttpContext http, AppDbContext db, SharedFileStorage storage, TimeProvider clock)
    {
        var request = http.Request;
        if (!IsOctetStream(request.ContentType))
        {
            return AuthEndpoints.InvalidRequest();
        }

        if (!TryReadFileName(request.Headers[FileNameHeader].ToString(), out var fileName))
        {
            return AuthEndpoints.BadRequest(SharedFileRules.InvalidFileNameMessage);
        }

        // The admin port keeps Kestrel's 30 MB default. A file to share has no size cap, so the limit is lifted for this request only.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
        {
            bodyLimit.MaxRequestBodySize = null;
        }

        if (!storage.HasRoomFor(request.ContentLength ?? 0))
        {
            return NoDiskSpace();
        }

        var ct = http.RequestAborted;
        var tempPath = storage.NewTempPath();
        try
        {
            ReceivedFile? received;
            try
            {
                received = await ReceiveAsync(request.Body, tempPath, storage, ct);
            }
            catch (BadHttpRequestException)
            {
                return AuthEndpoints.BadRequest(UploadIncompleteMessage);
            }

            if (received is null)
            {
                return NoDiskSpace();
            }

            var stored = await storage.ExclusiveAsync(
                () => StoreAsync(db, storage, tempPath, fileName, received, clock.GetUtcNow(), ct),
                ct);
            return Results.Json(
                new SharedFileUploadResponse(stored.Id, stored.Name, stored.SizeBytes, stored.Sha256),
                statusCode: StatusCodes.Status201Created);
        }
        finally
        {
            // Whatever happened, including an aborted upload, a temp file that was not moved into place is removed.
            storage.TryDelete(tempPath);
        }
    }

    internal static bool IsOctetStream(string? contentType) =>
        contentType?.Split(';')[0].Trim().Equals(OctetStream, StringComparison.OrdinalIgnoreCase) == true;

    // The header carries the original name URL-encoded, so any name survives the trip. It is decoded, then cleaned and checked.
    internal static bool TryReadFileName(string? header, out string fileName)
    {
        fileName = "";
        if (string.IsNullOrEmpty(header) || header.Length > MaxEncodedNameLength)
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(header);
        }
        catch (UriFormatException)
        {
            return false;
        }

        return SharedFileRules.TryCleanFileName(decoded, out fileName);
    }

    // Writes the body to the temp file while hashing it. Null when the disk would drop below the minimum free space, or fills up.
    private static async Task<ReceivedFile?> ReceiveAsync(Stream body, string path, SharedFileStorage storage, CancellationToken ct)
    {
        try
        {
            return await WriteAndHashAsync(body, path, storage, ct);
        }
        catch (IOException exception) when (IsDiskFull(exception))
        {
            return null;
        }
    }

    // ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL, as Windows reports them through an IOException.
    internal static bool IsDiskFull(IOException exception) =>
        exception.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027);

    private static async Task<ReceivedFile?> WriteAndHashAsync(Stream body, string path, SharedFileStorage storage, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, ReadChunkBytes, FileOptions.Asynchronous);
        var buffer = new byte[ReadChunkBytes];
        long total = 0;
        long nextDiskCheck = DiskCheckIntervalBytes;
        while (true)
        {
            var read = await body.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            hash.AppendData(buffer, 0, read);
            total += read;
            if (total >= nextDiskCheck)
            {
                // What is written already uses its space, so only the minimum is checked here.
                if (!storage.HasRoomFor(0))
                {
                    return null;
                }

                nextDiskCheck = total + DiskCheckIntervalBytes;
            }
        }

        return new ReceivedFile(Convert.ToHexStringLower(hash.GetHashAndReset()), total);
    }

    // Runs inside the storage gate. The same bytes already stored reuse the row, which counts as uploaded now and takes the new name.
    private static async Task<SharedFile> StoreAsync(
        AppDbContext db,
        SharedFileStorage storage,
        string tempPath,
        string fileName,
        ReceivedFile received,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // The path comes from the hash the server computed, never from the name the administrator sent.
        var finalPath = storage.PathFor(received.Sha256);
        var existing = await db.SharedFiles.FirstOrDefaultAsync(f => f.Sha256 == received.Sha256, ct);
        var fileWasStored = File.Exists(finalPath);
        if (!fileWasStored)
        {
            File.Move(tempPath, finalPath);
        }

        try
        {
            var stored = existing ?? new SharedFile { Sha256 = received.Sha256 };
            stored.Name = fileName;
            stored.SizeBytes = received.SizeBytes;
            stored.CreatedUtc = now;
            if (existing is null)
            {
                db.SharedFiles.Add(stored);
            }

            await db.SaveChangesAsync(ct);
            return stored;
        }
        catch
        {
            // Without a row the file just moved in belongs to nothing, so it is removed.
            if (!fileWasStored && existing is null)
            {
                storage.TryDelete(finalPath);
            }

            throw;
        }
    }

    private static IResult NoDiskSpace() =>
        Results.Json(new ErrorResponse(NoDiskSpaceMessage), statusCode: StatusCodes.Status507InsufficientStorage);
}
