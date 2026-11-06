using LabGuard.Server.Auth;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using LabGuard.Server.Pcs;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Requests;

// Sent as {} by accept and deny. The body is required so a cross-origin form can't send it.
public record RegistrationDecisionRequest;

public record RegistrationRequestResponse(int Id, string Name, string? IpAddress, string PairingNumber, DateTimeOffset RequestedAt);
public record RegistrationRequestListResponse(List<RegistrationRequestResponse> Requests);

public static class RegistrationRequestEndpoints
{
    private const string NotPendingMessage = "Registration request not found, or it has already been answered or has expired.";

    public static IEndpointRouteBuilder MapRegistrationRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/registration-requests");
        group.MapGet("/", ListAsync);
        group.MapPost("/{id:int}/accept", AcceptAsync).AddEndpointFilter<PcStorageErrorFilter>();
        group.MapPost("/{id:int}/deny", DenyAsync).AddEndpointFilter<PcStorageErrorFilter>();
        return app;
    }

    // Only pending, unexpired and revealed requests, oldest first. An unrevealed request has no pairing number yet, so it stays hidden.
    // Polled in the background, so the response is never cached.
    private static async Task<IResult> ListAsync(HttpContext http, AppDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var pendingSince = PcRegistrationRequest.PendingSince(clock.GetUtcNow());
        var requests = await db.PcRegistrationRequests
            .AsNoTracking()
            .Where(r => r.Status == RegistrationRequestStatus.Pending && r.Nonce != null && r.RequestedAtUtc >= pendingSince)
            .OrderBy(r => r.RequestedAtUtc)
            .ThenBy(r => r.Id)
            .Select(r => new RegistrationRequestResponse(r.Id, r.Name, r.IpAddress, r.PairingNumber!, r.RequestedAtUtc))
            .ToListAsync(cancellationToken);

        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new RegistrationRequestListResponse(requests));
    }

    // Claims the request before anything else. That is the transaction's first write, so a concurrent decision waits
    // instead of deadlocking, and only one caller can ever claim it. A rejection that is the request's own fault
    // (the name is taken) denies it and commits, so the PC is told and it leaves the list. Other rejections roll back.
    private static async Task<IResult> AcceptAsync(int id, HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (await AuthEndpoints.ReadBodyAsync<RegistrationDecisionRequest>(http.Request) is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        var pendingSince = PcRegistrationRequest.PendingSince(now);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.PcRegistrationRequests
            .Where(r => r.Id == id && r.Status == RegistrationRequestStatus.Pending && r.Nonce != null && r.RequestedAtUtc >= pendingSince)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.Status, RegistrationRequestStatus.Approved), ct);
        if (claimed == 0)
        {
            return NotPending();
        }

        var request = await db.PcRegistrationRequests.AsNoTracking().SingleAsync(r => r.Id == id, ct);
        var nameTaken = await db.Pcs.AnyAsync(p => p.Name == request.Name, ct);
        if (!nameTaken)
        {
            // The PC already holds the token whose hash the request carries, so it can heartbeat from now on.
            db.Pcs.Add(new Pc
            {
                Name = request.Name,
                TokenHash = request.TokenHash,
                IpAddress = request.IpAddress,
                RegisteredAtUtc = now,
                LastSeenUtc = now,
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException exception) when (ClientEndpoints.IsUniqueViolation(exception))
            {
                // The name was taken between the check above and this insert.
                nameTaken = true;
            }
        }

        if (nameTaken)
        {
            await db.PcRegistrationRequests
                .Where(r => r.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.Status, RegistrationRequestStatus.Denied), ct);
            await transaction.CommitAsync(ct);
            return ClientEndpoints.NameTaken();
        }

        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DenyAsync(int id, HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (await AuthEndpoints.ReadBodyAsync<RegistrationDecisionRequest>(http.Request) is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var pendingSince = PcRegistrationRequest.PendingSince(clock.GetUtcNow());
        var denied = await db.PcRegistrationRequests
            .Where(r => r.Id == id && r.Status == RegistrationRequestStatus.Pending && r.Nonce != null && r.RequestedAtUtc >= pendingSince)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.Status, RegistrationRequestStatus.Denied), http.RequestAborted);
        return denied == 0 ? NotPending() : Results.NoContent();
    }

    private static IResult NotPending() =>
        Results.Json(new ErrorResponse(NotPendingMessage), statusCode: StatusCodes.Status404NotFound);
}
