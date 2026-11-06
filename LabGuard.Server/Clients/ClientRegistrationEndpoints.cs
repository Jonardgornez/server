using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Clients;

public record CreateRegistrationRequestBody(string? Name, string? Nonce);
public record CreateRegistrationRequestResponse(string Token);
public record RegistrationStatusResponse(string Status);

// A new PC asks to join and waits for the administrator. Until it is accepted, its token only works for status and cancel.
public static partial class ClientEndpoints
{
    public const int MaxPendingRegistrationRequests = 100;

    private const int TokenBytes = 32;
    private const string PendingFullMessage = "Too many PCs are waiting for approval. Try again later.";
    private static readonly TimeSpan RegistrationGateWaitTimeout = TimeSpan.FromSeconds(3);

    // Finished and expired requests are kept for an hour, so the administrator and the PC can still see the outcome.
    private static readonly TimeSpan RequestRetention = TimeSpan.FromHours(1);

    // Serializes creating requests, so concurrent requests can't count past the pending limit together.
    private static readonly SemaphoreSlim RegistrationGate = new(1, 1);

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\z")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex NoncePattern();

    private static async Task<IResult> CreateRegistrationRequestAsync(
        HttpContext http,
        AppDbContext db,
        TimeProvider clock,
        PcChannelStore channel)
    {
        var body = await AuthEndpoints.ReadBodyAsync<CreateRegistrationRequestBody>(http.Request);
        if (body is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var name = body.Name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > MaxNameLength || !NamePattern().IsMatch(name))
        {
            return AuthEndpoints.BadRequest(
                $"PC name must be 1 to {MaxNameLength} characters: letters, numbers, dots, underscores and hyphens only.");
        }

        if (body.Nonce is not { } nonce || !NoncePattern().IsMatch(nonce))
        {
            return AuthEndpoints.BadRequest("Nonce must be 64 lowercase hex characters.");
        }

        if (!await RegistrationGate.WaitAsync(RegistrationGateWaitTimeout, http.RequestAborted))
        {
            return ServerBusy();
        }

        try
        {
            return await StoreRegistrationRequestAsync(http, db, clock, channel, name, nonce);
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
        PcChannelStore channel,
        string name,
        string nonce)
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

        if (await db.Pcs.AnyAsync(p => p.Name == name, ct))
        {
            return NameTaken();
        }

        var pendingSince = PcRegistrationRequest.PendingSince(now);
        var pendingCount = await db.PcRegistrationRequests
            .CountAsync(r => r.Status == RegistrationRequestStatus.Pending && r.RequestedAtUtc >= pendingSince, ct);
        if (pendingCount >= MaxPendingRegistrationRequests)
        {
            return Results.Json(new ErrorResponse(PendingFullMessage), statusCode: StatusCodes.Status429TooManyRequests);
        }

        // The token is what the PC keeps. Only its hash is stored; accepting the request moves the hash to the new Pc.
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        db.PcRegistrationRequests.Add(new PcRegistrationRequest
        {
            Name = name,
            IpAddress = address,
            Nonce = nonce,
            PairingNumber = PairingNumber.Compute(channel.Certificate, Convert.FromHexString(nonce)),
            TokenHash = HashToken(token),
            RequestedAtUtc = now,
            Status = RegistrationRequestStatus.Pending,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Results.Json(new CreateRegistrationRequestResponse(token), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> RegistrationStatusAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (ReadBearerToken(http.Request) is not { } token)
        {
            return NotRegistered();
        }

        var tokenHash = HashToken(token);
        var request = await db.PcRegistrationRequests
            .AsNoTracking()
            .Where(r => r.TokenHash == tokenHash)
            .Select(r => new { r.Status, r.RequestedAtUtc })
            .FirstOrDefaultAsync(http.RequestAborted);
        if (request is null)
        {
            return NotRegistered();
        }

        return Results.Ok(new RegistrationStatusResponse(PublicStatus(request.Status, request.RequestedAtUtc, clock.GetUtcNow())));
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

    // Only a pending request changes. An answered request stays as it is and still answers 204.
    private static async Task<IResult> CancelRegistrationRequestAsync(HttpContext http, AppDbContext db)
    {
        if (ReadBearerToken(http.Request) is not { } token)
        {
            return NotRegistered();
        }

        var tokenHash = HashToken(token);
        var ct = http.RequestAborted;
        if (!await db.PcRegistrationRequests.AnyAsync(r => r.TokenHash == tokenHash, ct))
        {
            return NotRegistered();
        }

        await db.PcRegistrationRequests
            .Where(r => r.TokenHash == tokenHash && r.Status == RegistrationRequestStatus.Pending)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.Status, RegistrationRequestStatus.Cancelled), ct);
        return Results.NoContent();
    }
}
