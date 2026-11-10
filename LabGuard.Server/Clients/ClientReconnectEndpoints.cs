using System.Buffers.Text;
using System.Security.Cryptography;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Pcs;
using LabGuard.Server.Requests;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Clients;

public record ReconnectResponse(string Token, string PcName);

// A reinstalled PC has lost its token. Lab PCs have static IP addresses, so the address identifies the machine: an offline PC
// last seen at the caller's address gets a new token and keeps its name, lock, internet block and history. A removed PC has no
// row left to match, so it registers again the normal way (pairing number and approval). An online PC is never taken over.
public static partial class ClientEndpoints
{
    private const string ReconnectOnlineMessage = "A PC with this address is online right now.";
    private const string ReconnectNotRegisteredMessage = "This PC isn't registered yet.";

    // Serialized with the other PC writes, so two reconnects can't both see the PC as offline. The conditional update below is the real guard.
    private static async Task<IResult> ReconnectAsync(HttpContext http, AppDbContext db, TimeProvider clock, ILoggerFactory loggerFactory)
    {
        if (RemoteAddress(http)?.ToString() is not { } address)
        {
            return ReconnectNotRegistered();
        }

        var ct = http.RequestAborted;
        var logger = loggerFactory.CreateLogger(nameof(ClientEndpoints));
        return await InGateAsync(() => ReconnectInGateAsync(http, db, address, clock, logger, ct), ct);
    }

    // One transaction. Any answer other than 200 returns before the commit, so nothing is changed.
    private static async Task<IResult> ReconnectInGateAsync(
        HttpContext http,
        AppDbContext db,
        string address,
        TimeProvider clock,
        ILogger logger,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // The PC with this address that was seen most recently. IpAddress is set when the PC is approved and kept current by its heartbeats.
        var candidate = await db.Pcs
            .AsNoTracking()
            .Where(p => p.IpAddress == address)
            .OrderByDescending(p => p.LastSeenUtc)
            .Select(p => new { p.Id, p.Name, p.TokenHash, p.LastSeenUtc })
            .FirstOrDefaultAsync(ct);
        if (candidate is null)
        {
            return ReconnectNotRegistered();
        }

        if (PcEndpoints.IsOnline(candidate.LastSeenUtc, now))
        {
            return ReconnectOnline();
        }

        // The old token stops working here. Only the hash is stored, as for every PC.
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        var tokenHash = HashToken(token);
        var offlineSince = now - PcEndpoints.OnlineWindow;
        var reconnected = await db.Pcs
            .Where(p => p.Id == candidate.Id
                && p.TokenHash == candidate.TokenHash
                && (p.LastSeenUtc <= offlineSince || p.LastSeenUtc > now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.TokenHash, tokenHash)
                .SetProperty(p => p.LastSeenUtc, now), ct);
        if (reconnected == 0)
        {
            // Another request changed the PC after it was read, so it is no longer offline.
            return ReconnectOnline();
        }

        // The old install's sessions and login request can't continue. An expired session is recorded as expired first.
        await SessionLifecycle.EndExpiredForPcAsync(db, candidate.Id, now, ct);
        await SessionLifecycle.EndForPcAsync(db, candidate.Id, SessionLifecycle.ConnectionLost, now, ct);
        await LoginRequestLifecycle.CancelPendingForPcAsync(db, candidate.Id, LoginRequestLifecycle.PcOffline, now, ct);

        // A pending registration from this address is superseded by the reconnect.
        await db.PcRegistrationRequests
            .Where(r => r.IpAddress == address && r.Status == RegistrationRequestStatus.Pending)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.Status, RegistrationRequestStatus.Cancelled), ct);

        await transaction.CommitAsync(ct);

        // The token is a credential, so only the PC and its address are logged.
        logger.LogInformation("PC {PcId} ({PcName}) reconnected from {Address}", candidate.Id, candidate.Name, address);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new ReconnectResponse(token, candidate.Name));
    }

    private static IResult ReconnectOnline() =>
        Results.Json(new ErrorResponse(ReconnectOnlineMessage), statusCode: StatusCodes.Status409Conflict);

    private static IResult ReconnectNotRegistered() =>
        Results.Json(new ErrorResponse(ReconnectNotRegisteredMessage), statusCode: StatusCodes.Status404NotFound);
}
