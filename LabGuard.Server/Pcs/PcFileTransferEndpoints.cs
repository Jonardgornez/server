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

// One transfer as the administrator sees it. Status is a SharedFileRules status, and a transfer never handed over in time shows as expired.
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
    DateTimeOffset UpdatedUtc);
public record FileTransfersResponse(FileTransferResponse[] Transfers);

public static partial class PcEndpoints
{
    private const string SharedFileNotFoundMessage = "File not found.";
    private const string UnknownPcsMessage = "One or more of the chosen PCs no longer exist.";
    private const string FolderMessage = "Choose Desktop, Documents or Downloads.";
    private const string DecisionMessage = "Choose overwrite, keep both or skip.";

    private static IEndpointRouteBuilder MapFileTransferEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/files", SendFileAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapGet("/files/transfers", TransfersAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/files/transfers/decision", DecideTransfersAsync).AddEndpointFilter<PcStorageErrorFilter>();
        return group;
    }

    // Creates one waiting transfer per PC. Each PC picks it up on its next heartbeat, so an offline PC gets it when it comes back within the hour.
    private static async Task<IResult> SendFileAsync(HttpContext http, AppDbContext db, TimeProvider clock)
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
        var pcNames = await db.Pcs
            .AsNoTracking()
            .Where(p => pcIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        if (pcNames.Count != pcIds.Length)
        {
            return AuthEndpoints.BadRequest(UnknownPcsMessage);
        }

        var transfers = pcIds.Select(pcId => new PcFileTransfer
        {
            PcId = pcId,
            PcName = pcNames[pcId],
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

        return Results.Ok(new FileTransfersResponse(transfers.Select(t => ToTransferResponse(t, now)).ToArray()));
    }

    // The transfers named by "ids", a comma-separated list. Ids that match no transfer are left out of the answer.
    private static async Task<IResult> TransfersAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (ParseTransferIds(http.Request.Query["ids"].ToString()) is not { } ids)
        {
            return AuthEndpoints.InvalidRequest();
        }

        return Results.Ok(new FileTransfersResponse(await ReadTransfersAsync(db, ids, clock.GetUtcNow(), http.RequestAborted)));
    }

    // Records the administrator's answer on each listed transfer still waiting for one. The PC applies it on its next heartbeat.
    // Transfers in any other status are returned as they are.
    private static async Task<IResult> DecideTransfersAsync(HttpContext http, AppDbContext db, TimeProvider clock)
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

        return Results.Ok(new FileTransfersResponse(await ReadTransfersAsync(db, transferIds, now, ct)));
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

    private static async Task<FileTransferResponse[]> ReadTransfersAsync(AppDbContext db, long[] ids, DateTimeOffset now, CancellationToken ct) =>
        (await db.PcFileTransfers
            .AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .OrderBy(t => t.Id)
            .ToListAsync(ct))
            .Select(t => ToTransferResponse(t, now))
            .ToArray();

    private static FileTransferResponse ToTransferResponse(PcFileTransfer transfer, DateTimeOffset now) =>
        new(
            transfer.Id,
            transfer.PcId,
            transfer.PcName,
            transfer.FileName,
            transfer.SizeBytes,
            transfer.Folder,
            transfer.Subfolder,
            SharedFileRules.EffectiveStatus(transfer.Status, transfer.CreatedUtc, now),
            transfer.Decision,
            transfer.SavedAs,
            transfer.Error,
            transfer.CreatedUtc,
            transfer.UpdatedUtc);
}
