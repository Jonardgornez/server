using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Clients;

public record CreateRegistrationRequestBody(string? Name, string? Commitment);
public record CreateRegistrationRequestResponse(string Token, string ServerNonce);
public record RevealRegistrationRequestBody(string? Nonce);
// Name is the PC's assigned name, sent once the request is approved; it is null before that.
public record RegistrationStatusResponse(string Status, string? Name = null);

// A new PC asks to join and waits for the administrator. Until it is accepted, its token only works for status, reveal and cancel.
// The PC commits to its nonce first and gets the server's nonce back. Then it reveals its nonce, which must match the commitment.
// Each side fixes its random value before seeing the other's, so neither can steer the pairing number.
public static partial class ClientEndpoints
{
    public const int MaxPendingRegistrationRequests = 100;

    private const int TokenBytes = 32;
    private const int ServerNonceBytes = 32;
    private const string PendingFullMessage = "Too many PCs are waiting for approval. Try again later.";
    private const string BadHexMessage = "Nonce must be 64 lowercase hex characters.";
    private const string CommitmentMismatchMessage = "The nonce doesn't match the commitment. The request has been cancelled.";
    private const string RevealRefusedMessage = "The nonce can't be revealed. The request is no longer pending, or its nonce was already revealed.";
    private static readonly TimeSpan RegistrationGateWaitTimeout = TimeSpan.FromSeconds(3);

    // Finished and expired requests are kept for an hour, so the administrator and the PC can still see the outcome.
    private static readonly TimeSpan RequestRetention = TimeSpan.FromHours(1);

    // Serializes creating requests, so concurrent requests can't count past the pending limit together.
    private static readonly SemaphoreSlim RegistrationGate = new(1, 1);

    // 32 bytes as 64 lowercase hex characters: a commitment, a nonce or a server nonce.
    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex Bytes32HexPattern();

    private static async Task<IResult> CreateRegistrationRequestAsync(
        HttpContext http,
        AppDbContext db,
        TimeProvider clock)
    {
        var body = await AuthEndpoints.ReadBodyAsync<CreateRegistrationRequestBody>(http.Request);
        if (body is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        // The server names the PC (see PcNaming), so a name an older client still sends is ignored.
        if (body.Commitment is not { } commitment || !Bytes32HexPattern().IsMatch(commitment))
        {
            return AuthEndpoints.BadRequest(BadHexMessage);
        }

        if (!await RegistrationGate.WaitAsync(RegistrationGateWaitTimeout, http.RequestAborted))
        {
            return ServerBusy();
        }

        try
        {
            return await StoreRegistrationRequestAsync(http, db, clock, commitment);
        }
        finally
        {
            RegistrationGate.Release();
        }
    }

    // One transaction. Any rejection returns before the commit, so it also undoes the cleanup and cancellations.
    private static async Task<IResult> StoreRegistrationRequestAsync(
        HttpContext http,
        AppDbContext db,
        TimeProvider clock,
        string commitment)
    {
        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        var address = RemoteAddress(http)?.ToString();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Every pending row this old has also expired, so this only removes finished or expired rows.
        var retentionCutoff = now - RequestRetention;
        await db.PcRegistrationRequests
            .Where(r => r.RequestedAtUtc < retentionCutoff)
            .ExecuteDeleteAsync(ct);

        // A PC that asks again replaces its own earlier request.
        if (address is not null)
        {
            await db.PcRegistrationRequests
                .Where(r => r.IpAddress == address && r.Status == RegistrationRequestStatus.Pending)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.Status, RegistrationRequestStatus.Cancelled), ct);
        }

        var pendingSince = PcRegistrationRequest.PendingSince(now);
        var pendingCount = await db.PcRegistrationRequests
            .CountAsync(r => r.Status == RegistrationRequestStatus.Pending && r.RequestedAtUtc >= pendingSince, ct);
        if (pendingCount >= MaxPendingRegistrationRequests)
        {
            return Results.Json(new ErrorResponse(PendingFullMessage), statusCode: StatusCodes.Status429TooManyRequests);
        }

        // Runs under the registration gate, after this PC's own earlier request was cancelled, so it can get its number back.
        var name = await NextFreeNameAsync(db, pendingSince, excludeRequestId: null, ct);

        // The token is what the PC keeps. Only its hash is stored; accepting the request moves the hash to the new Pc.
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        var serverNonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(ServerNonceBytes));
        db.PcRegistrationRequests.Add(new PcRegistrationRequest
        {
            Name = name,
            IpAddress = address,
            Commitment = commitment,
            ServerNonce = serverNonce,
            TokenHash = HashToken(token),
            RequestedAtUtc = now,
            Status = RegistrationRequestStatus.Pending,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Results.Json(new CreateRegistrationRequestResponse(token, serverNonce), statusCode: StatusCodes.Status202Accepted);
    }

    // The lowest PC-<number> that no registered PC and no other waiting request uses.
    internal static async Task<string> NextFreeNameAsync(AppDbContext db, DateTimeOffset pendingSince, int? excludeRequestId, CancellationToken ct)
    {
        var used = await db.Pcs.Select(p => p.Name).ToListAsync(ct);
        used.AddRange(await db.PcRegistrationRequests
            .Where(r => r.Status == RegistrationRequestStatus.Pending && r.RequestedAtUtc >= pendingSince && r.Id != excludeRequestId)
            .Select(r => r.Name)
            .ToListAsync(ct));
        return PcNaming.NextFreeName(used);
    }

    // Only while the request is pending. The nonce must match the commitment; a wrong nonce cancels the request, and a revealed nonce is never replaced.
    private static async Task<IResult> RevealRegistrationRequestAsync(HttpContext http, AppDbContext db, TimeProvider clock, PcChannelStore channel)
    {
        if (ReadBearerToken(http.Request) is not { } token)
        {
            return NotRegistered();
        }

        var ct = http.RequestAborted;
        var tokenHash = HashToken(token);
        var request = await db.PcRegistrationRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TokenHash == tokenHash, ct);
        if (request is null)
        {
            return NotRegistered();
        }

        var body = await AuthEndpoints.ReadBodyAsync<RevealRegistrationRequestBody>(http.Request);
        if (body is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        if (body.Nonce is not { } nonce || !Bytes32HexPattern().IsMatch(nonce))
        {
            return AuthEndpoints.BadRequest(BadHexMessage);
        }

        var now = clock.GetUtcNow();
        var pendingSince = PcRegistrationRequest.PendingSince(now);
        if (request.Status != RegistrationRequestStatus.Pending || request.RequestedAtUtc < pendingSince)
        {
            return RevealRefused();
        }

        // The same nonce again means the PC never got the first answer, so it succeeds. A different one is refused.
        if (request.Nonce is not null)
        {
            return request.Nonce == nonce ? Results.NoContent() : RevealRefused();
        }

        var nonceBytes = Convert.FromHexString(nonce);
        if (Convert.ToHexStringLower(SHA256.HashData(nonceBytes)) != request.Commitment)
        {
            await db.PcRegistrationRequests
                .Where(r => r.Id == request.Id && r.Status == RegistrationRequestStatus.Pending && r.Nonce == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.Status, RegistrationRequestStatus.Cancelled), ct);
            return AuthEndpoints.BadRequest(CommitmentMismatchMessage);
        }

        var pairingNumber = PairingNumber.Compute(channel.Certificate, nonceBytes, Convert.FromHexString(request.ServerNonce));
        var revealed = await db.PcRegistrationRequests
            .Where(r => r.Id == request.Id
                && r.Status == RegistrationRequestStatus.Pending
                && r.Nonce == null
                && r.RequestedAtUtc >= pendingSince)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Nonce, nonce)
                .SetProperty(r => r.PairingNumber, pairingNumber), ct);
        if (revealed > 0)
        {
            return Results.NoContent();
        }

        // A retry can race the PC's own first reveal, and losing that race with the same nonce still succeeded.
        var storedNonce = await db.PcRegistrationRequests
            .Where(r => r.Id == request.Id && r.Status == RegistrationRequestStatus.Pending)
            .Select(r => r.Nonce)
            .FirstOrDefaultAsync(ct);
        return storedNonce == nonce ? Results.NoContent() : RevealRefused();
    }

    private static async Task<IResult> RegistrationStatusAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (ReadBearerToken(http.Request) is not { } token)
        {
            return NotRegistered();
        }

        var ct = http.RequestAborted;
        var tokenHash = HashToken(token);
        var request = await db.PcRegistrationRequests
            .AsNoTracking()
            .Where(r => r.TokenHash == tokenHash)
            .Select(r => new { r.Status, r.RequestedAtUtc })
            .FirstOrDefaultAsync(ct);
        // The registered PC's own name, from the Pc row, so it is the name the administrator sees even after a rename.
        var pcName = await db.Pcs.Where(p => p.TokenHash == tokenHash).Select(p => p.Name).FirstOrDefaultAsync(ct);
        if (request is null)
        {
            // The request row may have been cleaned up after the PC was accepted. The registered PC is proof enough.
            return pcName is not null
                ? Results.Ok(new RegistrationStatusResponse("approved", pcName))
                : NotRegistered();
        }

        var status = PublicStatus(request.Status, request.RequestedAtUtc, clock.GetUtcNow());
        return Results.Ok(new RegistrationStatusResponse(status, status == "approved" ? pcName : null));
    }

    // Cancelled reads as expired, so the PC only ever sees pending, approved, denied or expired.
    internal static string PublicStatus(RegistrationRequestStatus status, DateTimeOffset requestedAt, DateTimeOffset now) =>
        status switch
        {
            RegistrationRequestStatus.Pending when requestedAt < PcRegistrationRequest.PendingSince(now) => "expired",
            RegistrationRequestStatus.Pending => "pending",
            RegistrationRequestStatus.Approved => "approved",
            RegistrationRequestStatus.Denied => "denied",
            RegistrationRequestStatus.Cancelled => "expired",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown registration status."),
        };

    // Only a pending request changes. A request that was already approved answers 409 so the PC learns it was accepted.
    // Any other answered request stays as it is and still answers 204.
    private static async Task<IResult> CancelRegistrationRequestAsync(HttpContext http, AppDbContext db)
    {
        if (ReadBearerToken(http.Request) is not { } token)
        {
            return NotRegistered();
        }

        var tokenHash = HashToken(token);
        var ct = http.RequestAborted;
        var cancelled = await db.PcRegistrationRequests
            .Where(r => r.TokenHash == tokenHash && r.Status == RegistrationRequestStatus.Pending)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.Status, RegistrationRequestStatus.Cancelled), ct);
        if (cancelled > 0)
        {
            return Results.NoContent();
        }

        // Nothing was pending, and a finished request never goes back to pending, so this status is the final answer.
        var request = await db.PcRegistrationRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TokenHash == tokenHash, ct);
        if (request is null)
        {
            return NotRegistered();
        }

        return request.Status == RegistrationRequestStatus.Approved
            ? Results.Json(new RegistrationStatusResponse("approved"), statusCode: StatusCodes.Status409Conflict)
            : Results.NoContent();
    }

    private static IResult RevealRefused() =>
        Results.Json(new ErrorResponse(RevealRefusedMessage), statusCode: StatusCodes.Status409Conflict);
}
