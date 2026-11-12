using System.Security.Claims;
using System.Security.Cryptography;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Pcs;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.ClientUpdates;

// The stored release as the administrator sees it. Sha256 is the hash of the file, so a PC can check what it downloaded.
public record ClientReleaseResponse(string Version, string Sha256, long SizeBytes, DateTimeOffset UploadedAtUtc);
// Release is null when no installer has been uploaded.
public record ClientReleaseStatusResponse(ClientReleaseResponse? Release);

// The administrator's installer: one release, uploaded as the raw file. Every PC runs what this installs, as SYSTEM.
public static class ClientUpdateEndpoints
{
    public const string ProductName = "Lykantra Client";
    public const string WrongInstallerMessage = "Choose the Lykantra Client installer (LykantraSetup.exe).";
    public const string TooLargeMessage = "The installer must be 150 MB or smaller.";
    public const string NoReleaseMessage = "Upload a client installer first.";
    // Uploads are rare and each one is 75 MB or more of disk writes, so the administrator gets a few an hour.
    public const int MaxUploadsPerHour = 10;
    public const string UploadLimitMessage = "Too many installer uploads. Try again in an hour.";

    private const int ReadChunkBytes = 80 * 1024;
    private static readonly byte[] MzSignature = "MZ"u8.ToArray();

    private enum ReceiveOutcome
    {
        Received,
        TooLarge,
        NotInstaller,
    }

    private sealed record ReceivedInstaller(ReceiveOutcome Outcome, string Sha256 = "", long SizeBytes = 0);

    public static IServiceCollection AddClientUpdates(this IServiceCollection services)
    {
        services.AddSingleton<IInstallerInspector, PeVersionInstallerInspector>();
        services.AddSingleton<ClientUploadLimiter>();
        services.AddSingleton(sp => new ClientReleaseFiles(
            ClientReleaseFiles.ResolveFolder(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>()),
            sp.GetRequiredService<ILogger<ClientReleaseFiles>>()));
        return services;
    }

    public static IEndpointRouteBuilder MapClientUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/client-updates");
        // The answers name the stored installer, so they are never cached.
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.AddEndpointFilter<PcStorageErrorFilter>();
        group.MapGet("/", GetAsync);
        group.MapPut("/", UploadAsync);
        group.MapDelete("/", DeleteAsync);
        return app;
    }

    private static async Task<IResult> GetAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var release = await db.ClientReleases.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return Results.Ok(new ClientReleaseStatusResponse(release is null ? null : ToResponse(release)));
    }

    // The body is the raw installer. It is streamed to a temporary file while its hash is computed, so it is never held in memory.
    private static async Task<IResult> UploadAsync(
        HttpContext http,
        AppDbContext db,
        ClientReleaseFiles files,
        IInstallerInspector inspector,
        ClientUploadLimiter uploadLimiter,
        TimeProvider clock,
        ILogger<ClientUpdateLog> logger)
    {
        // Checked here, after sign-in, so only signed-in uploads count. Before the body is read, so a refused one writes nothing.
        if (!uploadLimiter.TryAcquire(AdministratorName(http.User)))
        {
            return Results.Json(new ErrorResponse(UploadLimitMessage), statusCode: StatusCodes.Status429TooManyRequests);
        }

        // Kestrel's default body limit is 30 MB. Raised for this request only, before the body is read.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
        {
            bodyLimit.MaxRequestBodySize = ClientReleaseFiles.MaxInstallerBytes;
        }

        if (http.Request.ContentLength > ClientReleaseFiles.MaxInstallerBytes)
        {
            return TooLarge();
        }

        var ct = http.RequestAborted;
        var tempPath = files.NewTempPath();
        try
        {
            var received = await ReceiveAsync(http.Request.Body, tempPath, ct);
            return received.Outcome switch
            {
                ReceiveOutcome.TooLarge => TooLarge(),
                ReceiveOutcome.NotInstaller => WrongInstaller(),
                _ => await files.ExclusiveAsync(() => StoreAsync(http, db, files, inspector, clock, logger, tempPath, received, ct), ct),
            };
        }
        finally
        {
            // Whatever happened, a temporary file that was not moved into place is removed.
            files.TryDelete(tempPath);
        }
    }

    private static async Task<IResult> StoreAsync(
        HttpContext http,
        AppDbContext db,
        ClientReleaseFiles files,
        IInstallerInspector inspector,
        TimeProvider clock,
        ILogger<ClientUpdateLog> logger,
        string tempPath,
        ReceivedInstaller received,
        CancellationToken ct)
    {
        var info = inspector.Read(tempPath);
        if (info?.ProductName != ProductName || !ReleaseVersion.TryParseFileVersion(info.FileVersion, out var version))
        {
            return WrongInstaller();
        }

        var fileName = ClientReleaseFiles.FileNameFor(received.Sha256);
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Any version may replace the stored one: a rebuilt same version, a newer one, or an older one. Each PC still installs
        // only a version newer than its own (the watchdog service and the installer both refuse a downgrade or reinstall).
        var previous = await db.ClientReleases.FirstOrDefaultAsync(ct);

        // Read before the row changes: the row is the one being replaced, so its file is the one to remove afterwards.
        var previousFileName = previous?.FileName;
        File.Move(tempPath, files.PathFor(fileName), overwrite: true);
        try
        {
            var release = previous ?? new ClientRelease();
            release.Version = version.ToString();
            release.Sha256 = received.Sha256;
            release.SizeBytes = received.SizeBytes;
            release.FileName = fileName;
            release.UploadedAtUtc = now;
            if (previous is null)
            {
                db.ClientReleases.Add(release);
            }

            await db.SaveChangesAsync(ct);
            // Every PC offered an older version loses its target. Targets are only ever set to the stored version, so these are all of them.
            await ClientUpdateTargets.ClearWhereAsync(db, p => p.ClientUpdateTargetVersion != null && p.ClientUpdateTargetVersion != release.Version, ct);
            await transaction.CommitAsync(ct);

            // Everything in the folder but the new release goes: the replaced file, and any stray file.
            files.SweepExcept(fileName, now);

            logger.LogInformation(
                "Client installer {Version} (sha256 {Sha256}, {SizeBytes} bytes) uploaded by {Administrator} at {UploadedAtUtc}.",
                release.Version,
                release.Sha256,
                release.SizeBytes,
                AdministratorName(http.User),
                now);
            return Results.Ok(ToResponse(release));
        }
        catch
        {
            // The new file was moved in before the commit. Without a commit it belongs to no release, so it is removed.
            if (previousFileName != fileName)
            {
                files.TryDelete(files.PathFor(fileName));
            }

            throw;
        }
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext http,
        AppDbContext db,
        ClientReleaseFiles files,
        TimeProvider clock,
        ILogger<ClientUpdateLog> logger,
        CancellationToken cancellationToken)
    {
        // Idempotent: with nothing stored, the pending updates are still cleared and the answer is the same.
        var previous = await files.ExclusiveAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var removed = await db.ClientReleases.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            await db.ClientReleases.ExecuteDeleteAsync(cancellationToken);
            await ClientUpdateTargets.ClearWhereAsync(db, p => p.ClientUpdateTargetVersion != null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            files.SweepExcept(null, clock.GetUtcNow());
            return removed;
        }, cancellationToken);

        if (previous is not null)
        {
            logger.LogInformation("Client installer {Version} deleted by {Administrator}.", previous.Version, AdministratorName(http.User));
        }

        return Results.NoContent();
    }

    // Reads the body, stopping as soon as it would pass the limit or stops looking like a Windows program.
    private static async Task<ReceivedInstaller> ReceiveAsync(Stream body, string path, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, ReadChunkBytes, FileOptions.Asynchronous);
        var buffer = new byte[ReadChunkBytes];
        long total = 0;
        while (true)
        {
            var read = await body.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            if (!MatchesSignatureSoFar(buffer.AsSpan(0, read), total))
            {
                return new ReceivedInstaller(ReceiveOutcome.NotInstaller);
            }

            if (total + read > ClientReleaseFiles.MaxInstallerBytes)
            {
                return new ReceivedInstaller(ReceiveOutcome.TooLarge);
            }

            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            hash.AppendData(buffer, 0, read);
            total += read;
        }

        if (total < MzSignature.Length)
        {
            return new ReceivedInstaller(ReceiveOutcome.NotInstaller);
        }

        return new ReceivedInstaller(ReceiveOutcome.Received, Convert.ToHexStringLower(hash.GetHashAndReset()), total);
    }

    // A Windows program starts with "MZ". Checked chunk by chunk, so a wrong file is refused as soon as its first bytes arrive.
    private static bool MatchesSignatureSoFar(ReadOnlySpan<byte> chunk, long offset)
    {
        for (var i = 0; i < chunk.Length && offset + i < MzSignature.Length; i++)
        {
            if (chunk[i] != MzSignature[offset + i])
            {
                return false;
            }
        }

        return true;
    }

    // The signed-in administrator's user name, for the log. Never a secret.
    internal static string AdministratorName(ClaimsPrincipal user) =>
        user.FindFirst(ClaimTypes.Name)?.Value ?? "an administrator";

    private static ClientReleaseResponse ToResponse(ClientRelease release) =>
        new(release.Version, release.Sha256, release.SizeBytes, release.UploadedAtUtc);

    private static IResult WrongInstaller() => AuthEndpoints.BadRequest(WrongInstallerMessage);

    private static IResult TooLarge() =>
        Results.Json(new ErrorResponse(TooLargeMessage), statusCode: StatusCodes.Status413PayloadTooLarge);
}

// The log category of the administrator's client update actions, so their lines can be filtered by name.
public sealed class ClientUpdateLog;
