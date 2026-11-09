using System.Text.Json;
using System.Text.Json.Serialization;
using LabGuard.Server.Accounts;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;
using LoginRequest = LabGuard.Server.Data.LoginRequest;

namespace LabGuard.Server.Requests;

public record ApproveLoginRequestRequest(DefaultTimeRequest? Time);
public record DenyLoginRequestRequest;
public record DenyAllLoginRequestsRequest(List<int>? Ids);

public record LoginRequestResponse(
    int Id,
    string PcName,
    DateTimeOffset RequestedAt,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DefaultTimeResponse? Time,
    DateTimeOffset? DecidedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

public record LoginRequestListResponse(List<LoginRequestResponse> Pending, List<LoginRequestResponse> History);
public record DenyAllLoginRequestsResponse(List<LoginRequestResponse> Denied);

public static class LoginRequestEndpoints
{
    public const int MaxHistoryEntries = 50;
    public const int MaxDenyAllIds = 500;

    private const string ChooseTimeMessage = "Choose a time limit or open time.";
    private const string InvalidModeMessage = $"Time mode must be \"{AccountEndpoints.TimeLimitMode}\" or \"{AccountEndpoints.OpenTimeMode}\".";

    public static IEndpointRouteBuilder MapLoginRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/login-requests");
        group.MapGet("/", ListAsync);
        group.MapPost("/{id:int}/approve", ApproveAsync);
        group.MapPost("/{id:int}/deny", DenyAsync);
        group.MapPost("/deny-all", DenyAllAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var pending = await db.LoginRequests
            .AsNoTracking()
            .Where(r => r.Status == LoginRequestStatus.Pending)
            .OrderBy(r => r.RequestedAtUtc)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

        var history = await db.LoginRequests
            .AsNoTracking()
            .Where(r => r.Status != LoginRequestStatus.Pending)
            .OrderByDescending(r => r.DecidedAtUtc)
            .ThenByDescending(r => r.Id)
            .Take(MaxHistoryEntries)
            .ToListAsync(cancellationToken);

        return Results.Ok(new LoginRequestListResponse(
            pending.Select(ToResponse).ToList(),
            history.Select(ToResponse).ToList()));
    }

    private static async Task<IResult> ApproveAsync(int id, HttpContext http, AppDbContext db, TimeProvider clock)
    {
        var request = await AuthEndpoints.ReadBodyAsync<ApproveLoginRequestRequest>(http.Request);
        if (request is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        if (AccountEndpoints.ValidateDefaultTime(request.Time, out var minutes, ChooseTimeMessage, InvalidModeMessage) is { } timeError)
        {
            return AuthEndpoints.BadRequest(timeError);
        }

        return await LoginRequestApproval.ApproveAsync(db, id, minutes, clock, http.RequestAborted);
    }

    private static async Task<IResult> DenyAsync(int id, HttpContext http, AppDbContext db, TimeProvider clock)
    {
        // The body carries nothing; requiring JSON stops a cross-origin form from forging a denial.
        if (await AuthEndpoints.ReadBodyAsync<DenyLoginRequestRequest>(http.Request) is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var now = clock.GetUtcNow();
        return await DecideAsync(id, db, http.RequestAborted, setters => setters
            .SetProperty(r => r.Status, LoginRequestStatus.Denied)
            .SetProperty(r => r.ApprovedMinutes, (int?)null)
            .SetProperty(r => r.DecidedAtUtc, now));
    }

    private static async Task<IResult> DenyAllAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        var request = await AuthEndpoints.ReadBodyAsync<DenyAllLoginRequestsRequest>(http.Request);
        if (request is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        if (request.Ids is null)
        {
            return AuthEndpoints.BadRequest("Specify which requests to deny.");
        }

        if (request.Ids.Count > MaxDenyAllIds)
        {
            return AuthEndpoints.BadRequest($"You can deny at most {MaxDenyAllIds} requests at once.");
        }

        var ct = http.RequestAborted;
        var ids = request.Ids.Distinct().ToList();
        if (ids.Count == 0)
        {
            return Results.Ok(new DenyAllLoginRequestsResponse([]));
        }

        // One statement decides the pending rows and reports exactly which ones it changed (RETURNING),
        // so a request decided concurrently by another call is never claimed by this one.
        var idsJson = JsonSerializer.Serialize(ids);
        var decidedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var pending = nameof(LoginRequestStatus.Pending);
        var denied = nameof(LoginRequestStatus.Denied);
        var changedIds = await db.Database
            .SqlQuery<int>($"""
                UPDATE "LoginRequests"
                SET "Status" = {denied}, "ApprovedMinutes" = NULL, "DecidedAtUtc" = {decidedAt}
                WHERE "Status" = {pending} AND "Id" IN (SELECT "value" FROM json_each({idsJson}))
                RETURNING "Id" AS "Value"
                """)
            .ToListAsync(ct);

        var entries = await db.LoginRequests
            .AsNoTracking()
            .Where(r => changedIds.Contains(r.Id))
            .OrderBy(r => r.RequestedAtUtc)
            .ThenBy(r => r.Id)
            .ToListAsync(ct);

        return Results.Ok(new DenyAllLoginRequestsResponse(entries.Select(ToResponse).ToList()));
    }

    // Updates only while the request is still pending, so it is decided exactly once even under concurrency.
    private static async Task<IResult> DecideAsync(
        int id,
        AppDbContext db,
        CancellationToken cancellationToken,
        Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<LoginRequest>> setters)
    {
        var changed = await db.LoginRequests
            .Where(r => r.Id == id && r.Status == LoginRequestStatus.Pending)
            .ExecuteUpdateAsync(setters, cancellationToken);

        var request = await db.LoginRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (request is null)
        {
            return Results.Json(new ErrorResponse("Login request not found."), statusCode: StatusCodes.Status404NotFound);
        }

        return changed == 0
            ? Results.Json(new ErrorResponse("This request has already been decided."), statusCode: StatusCodes.Status409Conflict)
            : Results.Ok(ToResponse(request));
    }

    internal static LoginRequestResponse ToResponse(LoginRequest request) =>
        new(
            request.Id,
            request.PcName,
            request.RequestedAtUtc,
            LoginRequestLifecycle.WireStatus(request.Status),
            request.Status == LoginRequestStatus.Approved
                ? request.ApprovedMinutes is { } minutes
                    ? new DefaultTimeResponse(AccountEndpoints.TimeLimitMode, minutes)
                    : new DefaultTimeResponse(AccountEndpoints.OpenTimeMode, null)
                : null,
            request.DecidedAtUtc,
            request.Reason);
}
