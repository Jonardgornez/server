using System.Diagnostics.CodeAnalysis;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Sent by the admin. PcIds names the PCs and Blocked is the internet state to set on them. Both are required.
public record InternetBlockRequest(int[]? PcIds, bool? Blocked);
// Result is "ok", "not-found" or "server-pc". PcName is null for an id that matches no PC.
public record InternetBlockResult(int PcId, string? PcName, string Result);
public record InternetBlockResponse(InternetBlockResult[] Results);

public static partial class PcEndpoints
{
    // Most PCs one internet request may name. The lab has 60, so this only keeps a single request bounded.
    public const int MaxInternetBlockPcIds = 500;

    private const string InternetOkResult = "ok";
    private const string InternetNotFoundResult = "not-found";
    private const string InternetServerPcResult = "server-pc";

    // The PC applies the change on its next heartbeat, so an offline PC is updated too. The server's own PC is never blocked.
    private static async Task<IResult> SetInternetBlockAsync(HttpContext http, AppDbContext db, ILocalAddressSource localAddresses)
    {
        var request = await AuthEndpoints.ReadBodyAsync<InternetBlockRequest>(http.Request);
        if (request?.Blocked is not { } blocked || !IsValidInternetBlockIds(request.PcIds))
        {
            return AuthEndpoints.InvalidRequest();
        }

        // Fails closed: without the addresses, no PC can be shown not to be the server, so none is changed.
        if (localAddresses.ReadOrNull() is not { } machineAddresses)
        {
            return ConflictResponse(AddressUnreadableMessage);
        }

        var pcIds = request.PcIds;
        var ct = http.RequestAborted;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var pcs = await db.Pcs
            .AsNoTracking()
            .Where(p => pcIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.IpAddress })
            .ToDictionaryAsync(p => p.Id, ct);

        // Results follow the order the administrator listed the PCs in.
        var results = pcIds.Select(pcId => pcs.TryGetValue(pcId, out var pc)
                ? new InternetBlockResult(
                    pcId,
                    pc.Name,
                    ServerPcRule.IsServerPc(pc.IpAddress, machineAddresses) ? InternetServerPcResult : InternetOkResult)
                : new InternetBlockResult(pcId, null, InternetNotFoundResult))
            .ToArray();

        var okIds = results.Where(r => r.Result == InternetOkResult).Select(r => r.PcId).ToArray();
        await db.Pcs
            .Where(p => okIds.Contains(p.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.InternetBlocked, blocked), ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(new InternetBlockResponse(results));
    }

    // Each id is a positive PC id, listed once, and the list has at least one and at most the limit.
    private static bool IsValidInternetBlockIds([NotNullWhen(true)] int[]? pcIds) =>
        pcIds is { Length: > 0 and <= MaxInternetBlockPcIds } && pcIds.All(id => id > 0) && pcIds.Distinct().Count() == pcIds.Length;
}
