using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.SharedFiles;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Clients;

// A sent file as a heartbeat hands it to the PC. Decision is null for a new transfer, and the administrator's answer when
// the file already existed on the PC.
public record FileTransferDelivery(
    long Id,
    string FileName,
    long SizeBytes,
    string Sha256,
    string Folder,
    string Subfolder,
    string? Decision);

// The PC's report on a transfer. SavedAs is the name it saved under, and Error the reason it failed.
public record FileTransferStatusRequest(string? Status, string? SavedAs, string? Error);

// Hands sent files to the PC on its heartbeat, serves their bytes, and records how the PC got on. Only the PC a transfer
// was sent to can see it: another PC's transfer is answered exactly like a missing one.
public static partial class ClientEndpoints
{
    // A PC reports on each sent file once or twice, so this leaves room for several files.
    public const int MaxFileTransferReportsPerMinute = 60;
    public const string FileTransferReportRateLimitPolicy = "pc-file-transfer-report";
    // Each download can be any size, so a PC gets a few a minute and one at a time. That bounds the bytes it can pull.
    public const int MaxFileDownloadsPerMinute = 6;
    public const int MaxConcurrentFileDownloads = 1;
    public const string FileDownloadRateLimitPolicy = "pc-file-download";
    private const int DownloadBufferBytes = 80 * 1024;

    private const string TransferNotFoundMessage = "File transfer not found.";
    private const string TransferConflictMessage = "This file transfer can't move to that status now.";

    // Hands over this PC's oldest new transfer that is still within its lifetime, or one whose existing file has a decision.
    // One at a time: only while the PC is downloading or applying none. The update matches the status and decision that were
    // read and checks again that the PC is working on nothing, all in one statement, so two heartbeats at the same time can't
    // hand out the same transfer twice or two transfers at once.
    private static async Task<FileTransferDelivery[]> HandOverFileTransfersAsync(AppDbContext db, int pcId, DateTimeOffset now, CancellationToken ct)
    {
        var waitingSince = now - SharedFileRules.WaitingLifetime;
        if (await IsWorkingOnATransferAsync(db, pcId, ct))
        {
            return [];
        }

        var candidates = await db.PcFileTransfers
            .AsNoTracking()
            .Where(t => t.PcId == pcId
                && ((t.Status == SharedFileRules.Waiting && t.Decision == null && t.CreatedUtc > waitingSince)
                    || (t.Status == SharedFileRules.Exists && t.Decision != null)))
            .OrderBy(t => t.CreatedUtc)
            .ThenBy(t => t.Id)
            .Take(SharedFileRules.MaxHandOverPerHeartbeat)
            .ToListAsync(ct);

        var handedOver = new List<FileTransferDelivery>();
        foreach (var candidate in candidates)
        {
            var next = candidate.Status == SharedFileRules.Waiting ? SharedFileRules.Downloading : SharedFileRules.Applying;
            var changed = await db.PcFileTransfers
                .Where(t => t.Id == candidate.Id
                    && t.Status == candidate.Status
                    && t.Decision == candidate.Decision
                    && !db.PcFileTransfers.Any(other => other.PcId == pcId
                        && (other.Status == SharedFileRules.Downloading || other.Status == SharedFileRules.Applying)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.Status, next)
                    .SetProperty(t => t.UpdatedUtc, now), ct);
            if (changed > 0)
            {
                handedOver.Add(new FileTransferDelivery(
                    candidate.Id,
                    candidate.FileName,
                    candidate.SizeBytes,
                    candidate.Sha256,
                    candidate.Folder,
                    candidate.Subfolder,
                    candidate.Decision));
            }
        }

        return handedOver.ToArray();
    }

    private static Task<bool> IsWorkingOnATransferAsync(AppDbContext db, int pcId, CancellationToken ct) =>
        db.PcFileTransfers.AnyAsync(
            t => t.PcId == pcId && (t.Status == SharedFileRules.Downloading || t.Status == SharedFileRules.Applying),
            ct);

    // Records activity on a transfer the PC is still downloading or applying. Returns whether it matched.
    private static async Task<bool> RefreshTransferActivityAsync(
        AppDbContext db,
        long transferId,
        int pcId,
        DateTimeOffset now,
        CancellationToken ct) =>
        await db.PcFileTransfers
            .Where(t => t.Id == transferId
                && t.PcId == pcId
                && (t.Status == SharedFileRules.Downloading || t.Status == SharedFileRules.Applying))
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.UpdatedUtc, now), ct) > 0;

    // Streams the transfer's bytes, only to its own PC and only while it is downloading or applying the file. Each request
    // counts as activity, so a long download is timed from its latest request rather than from the hand-over.
    private static async Task<IResult> DownloadFileTransferAsync(
        long transferId,
        HttpContext http,
        AppDbContext db,
        SharedFileStorage storage,
        TimeProvider clock,
        ILogger<ActivityReportingStream> logger)
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

        // One update both checks the transfer and records the activity, so the status can't change between the two.
        if (!await RefreshTransferActivityAsync(db, transferId, pcId, clock.GetUtcNow(), ct))
        {
            return TransferNotFound();
        }

        var sha256 = await db.PcFileTransfers
            .AsNoTracking()
            .Where(t => t.Id == transferId && t.PcId == pcId)
            .Select(t => t.Sha256)
            .FirstOrDefaultAsync(ct);
        if (sha256 is null || !SharedFileStorage.IsSha256(sha256))
        {
            return TransferNotFound();
        }

        // The path comes from the stored hash alone, never from anything the PC sent. The file is opened here, so a file
        // removed a moment ago is a 404 rather than an error partway through the response.
        FileStream stream;
        try
        {
            stream = new FileStream(
                storage.PathFor(sha256),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                DownloadBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return TransferNotFound();
        }

        // While the bytes stream, the transfer's activity is refreshed about once a minute, so a long download is never
        // taken for a stopped PC. The refresh runs on this request's own database context.
        var reporting = new ActivityReportingStream(
            stream,
            clock,
            ActivityReportingStream.DefaultInterval,
            () => RefreshTransferActivityAsync(db, transferId, pcId, clock.GetUtcNow(), ct),
            logger);

        // Nothing may sniff or cache the file: the PC saves it as it is and never runs it.
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(reporting, "application/octet-stream", enableRangeProcessing: true);
    }

    // Records the PC's report. The move happens in one update that matches the statuses it may come from, so a report that
    // races another change, or comes twice, is refused with 409 instead of overwriting it.
    private static async Task<IResult> ReportFileTransferAsync(long transferId, HttpContext http, AppDbContext db, TimeProvider clock)
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

        var request = await AuthEndpoints.ReadBodyAsync<FileTransferStatusRequest>(http.Request);
        if (request?.Status is not { } status || SharedFileRules.AllowedFrom(status) is not { Length: > 0 } allowedFrom)
        {
            return AuthEndpoints.InvalidRequest();
        }

        string? savedAs = null;
        if (status == SharedFileRules.Saved && request.SavedAs is { } reportedName)
        {
            if (!SharedFileRules.IsValidSegment(reportedName, SharedFileRules.MaxSavedAsLength))
            {
                return AuthEndpoints.InvalidRequest();
            }

            savedAs = reportedName;
        }

        var transfer = await db.PcFileTransfers
            .AsNoTracking()
            .Where(t => t.Id == transferId && t.PcId == pcId)
            .Select(t => new { t.FileName })
            .FirstOrDefaultAsync(ct);
        if (transfer is null)
        {
            return TransferNotFound();
        }

        if (status == SharedFileRules.Saved)
        {
            savedAs ??= transfer.FileName;
        }

        var error = status == SharedFileRules.Failed ? SharedFileRules.CleanError(request.Error) : null;
        var now = clock.GetUtcNow();
        var changed = await db.PcFileTransfers
            .Where(t => t.Id == transferId && t.PcId == pcId && allowedFrom.Contains(t.Status))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Status, status)
                .SetProperty(t => t.SavedAs, savedAs)
                .SetProperty(t => t.Error, error)
                .SetProperty(t => t.UpdatedUtc, now), ct);
        return changed > 0
            ? Results.NoContent()
            : Results.Json(new ErrorResponse(TransferConflictMessage), statusCode: StatusCodes.Status409Conflict);
    }

    private static IResult TransferNotFound() =>
        Results.Json(new ErrorResponse(TransferNotFoundMessage), statusCode: StatusCodes.Status404NotFound);
}
