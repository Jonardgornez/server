using System.Globalization;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.SharedFiles;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Sent by the administrator to send an uploaded file to PCs. Folder and Subfolder fall back to the defaults when left out;
// an empty Subfolder saves straight into the folder.
public record PcFileSendRequest(int[]? Ids, int? SharedFileId, string? Folder, string? Subfolder);
// The administrator's answer for transfers whose file already exists on the PC.
public record PcFileDecisionRequest(long[]? TransferIds, string? Decision);
// The transfers the administrator stops.
public record PcFileCancelRequest(long[]? TransferIds);

// One transfer as the administrator sees it. Status is a SharedFileRules status, and a transfer never handed over in time shows as expired.
// WaitingReason says why a waiting transfer hasn't been handed over: the PC is offline, its client is too old, or it is busy with
// another transfer (BusyWith is that transfer's file name). Null for any other status, or when the PC will get it on its next heartbeat.
// PcClientVersion is the client version the PC last reported. BytesSent is how much of the file has reached the PC, only while
// it is downloading or applying the file and its download has been seen. It never exceeds SizeBytes.
public record FileTransferResponse(
    long Id,
    int PcId,
    string PcName,
    string FileName,
    long SizeBytes,
    string Folder,
    string Subfolder,
    string Status,
    string? Decision,
    string? SavedAs,
    string? Error,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? WaitingReason,
    string? BusyWith,
    string? PcClientVersion,
    long? BytesSent);
public record FileTransfersResponse(FileTransferResponse[] Transfers);

public static partial class PcEndpoints
{
    private const string SharedFileNotFoundMessage = "File not found.";
    private const string UnknownPcsMessage = "One or more of the chosen PCs no longer exist.";
    private const string NoOnlinePcsMessage = "None of these PCs are online.";
    private const string FolderMessage ="Choose Desktop, Documents or Downloads.";
    private const string DecisionMessage = "Choose overwrite, keep both or skip.";

    private static IEndpointRouteBuilder MapFileTransferEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/files", SendFileAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapGet("/files/transfers", TransfersAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/files/transfers/decision", DecideTransfersAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/files/transfers/cancel", CancelTransfersAsync).AddEndpointFilter<PcStorageErrorFilter>();
        return group;
    }

    // Creates one waiting transfer per online PC; offline PCs are skipped. Each PC picks it up on its next heartbeat, and one that
    // drops offline before then still gets it if it comes back within the hour.
    private static async Task<IResult> SendFileAsync(HttpContext http, AppDbContext db, FileTransferProgress progress, TimeProvider clock)
    {
        var request = await AuthEndpoints.ReadBodyAsync<PcFileSendRequest>(http.Request);
        if (request?.Ids is not { Length: > 0 and <= SharedFileRules.MaxPcsPerSend } ids || request.SharedFileId is not { } sharedFileId)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var folder = request.Folder ?? SharedFileRules.DefaultFolder;
        if (!SharedFileRules.IsKnownFolder(folder))
        {
            return AuthEndpoints.BadRequest(FolderMessage);
        }

        if (!SharedFileRules.TryNormalizeSubfolder(request.Subfolder, out var subfolder))
        {
            return AuthEndpoints.BadRequest(SharedFileRules.InvalidSubfolderMessage);
        }

        var pcIds = ids.Distinct().ToArray();
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Written first: the file counts as used now, and the write holds the database, so the cleanup can't remove it before
        // the transfers that need it are stored.
        var touched = await db.SharedFiles
            .Where(f => f.Id == sharedFileId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(f => f.CreatedUtc, now), ct);
        if (touched == 0)
        {
            return Results.Json(new ErrorResponse(SharedFileNotFoundMessage), statusCode: StatusCodes.Status404NotFound);
        }

        var file = await db.SharedFiles.AsNoTracking().SingleAsync(f => f.Id == sharedFileId, ct);
        var pcs = await db.Pcs
            .AsNoTracking()
            .Where(p => pcIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.LastSeenUtc })
            .ToDictionaryAsync(p => p.Id, ct);
        if (pcs.Count != pcIds.Length)
        {
            return AuthEndpoints.BadRequest(UnknownPcsMessage);
        }

        // Only an online PC gets the file. An offline one in the list is skipped, so a stale dashboard can't queue it.
        var onlinePcIds = pcIds.Where(pcId => IsOnline(pcs[pcId].LastSeenUtc, now)).ToArray();
        if (onlinePcIds.Length == 0)
        {
            return AuthEndpoints.BadRequest(NoOnlinePcsMessage);
        }

        var transfers = onlinePcIds.Select(pcId => new PcFileTransfer
        {
            PcId = pcId,
            PcName = pcs[pcId].Name,
            SharedFileId = file.Id,
            FileName = file.Name,
            SizeBytes = file.SizeBytes,
            Sha256 = file.Sha256,
            Folder = folder,
            Subfolder = subfolder,
            Status = SharedFileRules.Waiting,
            CreatedUtc = now,
            UpdatedUtc = now,
        }).ToList();
        db.PcFileTransfers.AddRange(transfers);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(new FileTransfersResponse(await ToTransferResponsesAsync(db, progress, transfers, now, ct)));
    }

    // The transfers named by "ids", a comma-separated list. Ids that match no transfer are left out of the answer.
    private static async Task<IResult> TransfersAsync(HttpContext http, AppDbContext db, FileTransferProgress progress, TimeProvider clock)
    {
        if (ParseTransferIds(http.Request.Query["ids"].ToString()) is not { } ids)
        {
            return AuthEndpoints.InvalidRequest();
        }

        return Results.Ok(new FileTransfersResponse(await ReadTransfersAsync(db, progress, ids, clock.GetUtcNow(), http.RequestAborted)));
    }

    // Records the administrator's answer on each listed transfer still waiting for one. The PC applies it on its next heartbeat.
    // Transfers in any other status are returned as they are.
    private static async Task<IResult> DecideTransfersAsync(HttpContext http, AppDbContext db, FileTransferProgress progress, TimeProvider clock)
    {
        var request = await AuthEndpoints.ReadBodyAsync<PcFileDecisionRequest>(http.Request);
        if (request?.TransferIds is not { Length: > 0 and <= SharedFileRules.MaxTransfersPerRequest } ids || request.Decision is not { } decision)
        {
            return AuthEndpoints.InvalidRequest();
        }

        if (!SharedFileRules.IsDecision(decision))
        {
            return AuthEndpoints.BadRequest(DecisionMessage);
        }

        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        var transferIds = ids.Distinct().ToArray();
        await db.PcFileTransfers
            .Where(t => transferIds.Contains(t.Id) && t.Status == SharedFileRules.Exists)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Decision, decision)
                .SetProperty(t => t.UpdatedUtc, now), ct);

        return Results.Ok(new FileTransfersResponse(await ReadTransfersAsync(db, progress, transferIds, now, ct)));
    }

    // Cancels each listed transfer the PC may still be working on. The PC can no longer download it or report on it, and a
    // client that already started it gives up quietly. Transfers in any other status are returned as they are. None of the listed
    // transfers is downloading any more, so their progress is forgotten.
    private static async Task<IResult> CancelTransfersAsync(HttpContext http, AppDbContext db, FileTransferProgress progress, TimeProvider clock)
    {
        var request = await AuthEndpoints.ReadBodyAsync<PcFileCancelRequest>(http.Request);
        if (request?.TransferIds is not { Length: > 0 and <= SharedFileRules.MaxTransfersPerRequest } ids || ids.Any(id => id <= 0))
        {
            return AuthEndpoints.InvalidRequest();
        }

        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        var transferIds = ids.Distinct().ToArray();
        await db.PcFileTransfers
            .Where(t => transferIds.Contains(t.Id) && SharedFileRules.OpenStatuses.Contains(t.Status))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.Status, SharedFileRules.Cancelled)
                .SetProperty(t => t.UpdatedUtc, now), ct);
        progress.Clear(transferIds);

        return Results.Ok(new FileTransfersResponse(await ReadTransfersAsync(db, progress, transferIds, now, ct)));
    }

    // Null unless the text is 1 to the limit of positive whole numbers separated by commas.
    internal static long[]? ParseTransferIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length > SharedFileRules.MaxTransfersPerRequest)
        {
            return null;
        }

        var ids = new List<long>(parts.Length);
        foreach (var part in parts)
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                return null;
            }

            ids.Add(id);
        }

        return ids.Distinct().ToArray();
    }

    private static async Task<FileTransferResponse[]> ReadTransfersAsync(
        AppDbContext db,
        FileTransferProgress progress,
        long[] ids,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var transfers = await db.PcFileTransfers
            .AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .OrderBy(t => t.Id)
            .ToListAsync(ct);
        return await ToTransferResponsesAsync(db, progress, transfers, now, ct);
    }

    // Reads, for the transfers' PCs, what the waiting reason needs: whether each PC is online, its client version, and the
    // transfer it is working on, by the same rules the heartbeat hand-over uses. That is one it is actively working on, or one
    // it abandoned that its next heartbeat hands over again.
    private static async Task<FileTransferResponse[]> ToTransferResponsesAsync(
        AppDbContext db,
        FileTransferProgress progress,
        IReadOnlyCollection<PcFileTransfer> transfers,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var pcIds = transfers.Select(t => t.PcId).Distinct().ToArray();
        var pcs = await db.Pcs
            .AsNoTracking()
            .Where(p => pcIds.Contains(p.Id))
            .Select(p => new TransferPc(p.Id, p.LastSeenUtc, p.ClientVersion))
            .ToDictionaryAsync(p => p.Id, ct);

        var pcTransfers = db.PcFileTransfers.AsNoTracking().Where(t => pcIds.Contains(t.PcId));
        var busyTransfers = await pcTransfers.Active(now)
            .Concat(pcTransfers.ToHandOverAgain(now))
            .OrderBy(t => t.CreatedUtc)
            .ThenBy(t => t.Id)
            .Select(t => new { t.PcId, t.FileName })
            .ToListAsync(ct);
        var busyWith = busyTransfers
            .GroupBy(t => t.PcId)
            .ToDictionary(group => group.Key, group => group.First().FileName);

        return transfers.Select(transfer =>
        {
            var status = SharedFileRules.EffectiveStatus(transfer.Status, transfer.CreatedUtc, now);
            var pc = pcs.GetValueOrDefault(transfer.PcId);
            var waitingReason = status == SharedFileRules.Waiting ? WaitingReasonOf(pc, busyWith.ContainsKey(transfer.PcId), now) : null;
            return new FileTransferResponse(
                transfer.Id,
                transfer.PcId,
                transfer.PcName,
                transfer.FileName,
                transfer.SizeBytes,
                transfer.Folder,
                transfer.Subfolder,
                status,
                transfer.Decision,
                transfer.SavedAs,
                transfer.Error,
                transfer.CreatedUtc,
                transfer.UpdatedUtc,
                waitingReason,
                waitingReason == SharedFileRules.BusyReason ? busyWith[transfer.PcId] : null,
                pc?.ClientVersion,
                BytesSentOf(progress, transfer, status));
        }).ToArray();
    }

    private static long? BytesSentOf(FileTransferProgress progress, PcFileTransfer transfer, string status)
    {
        if (status != SharedFileRules.Downloading && status != SharedFileRules.Applying)
        {
            return null;
        }

        return progress.BytesSent(transfer.Id) is { } bytes ? Math.Min(bytes, transfer.SizeBytes) : null;
    }

    // Offline first, then a client too old to receive files, then busy with another transfer.
    private static string? WaitingReasonOf(TransferPc? pc, bool isBusy, DateTimeOffset now)
    {
        if (pc is null)
        {
            return null;
        }

        if (!IsOnline(pc.LastSeenUtc, now))
        {
            return SharedFileRules.OfflineReason;
        }

        if (!SharedFileRules.IsFileClient(pc.ClientVersion))
        {
            return SharedFileRules.ClientTooOldReason;
        }

        return isBusy ? SharedFileRules.BusyReason : null;
    }

    private sealed record TransferPc(int Id, DateTimeOffset LastSeenUtc, string? ClientVersion);
}
