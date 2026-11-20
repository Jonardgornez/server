using LabGuard.Server.Auth;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Logs;

public record SessionLogItem(
    int Id,
    string PcName,
    string UserOrSource,
    string Source,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int DurationMinutes,
    string? EndReason);

public record SessionLogPageResponse(List<SessionLogItem> Items, int TotalItems, int Page, int PageSize);

// Admin only: the group doesn't allow anonymous access, so the fallback policy requires a signed-in administrator.
// The PC port never reaches /api/session-logs (ChannelGuard refuses it).
public static class SessionLogEndpoints
{
    // Sent as "true" when an export matched more rows than it returns. The admin page is same-origin, so no CORS exposure is needed.
    public const string ExportTruncatedHeader = "X-Export-Truncated";
    public const int MaxExportRows = 10_000;

    public static IEndpointRouteBuilder MapSessionLogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/session-logs");
        group.MapGet("/", ListAsync).AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapGet("/export", ExportAsync).AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapGet("/purge-preview", PurgePreviewAsync).AddEndpointFilter<ClientStorageErrorFilter>();
        group.MapPost("/purge", PurgeAsync).AddEndpointFilter<ClientStorageErrorFilter>();
        return app;
    }

    // How many sessions a purge with the same choice would delete, so the administrator sees it before confirming.
    private static async Task<IResult> PurgePreviewAsync(
        string? olderThanDays,
        HttpContext http,
        AppDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        http.Response.Headers.CacheControl = "no-store";

        if (!int.TryParse(olderThanDays, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var days)
            || !SessionLogPurge.IsAllowed(days))
        {
            return AuthEndpoints.BadRequest(SessionLogPurge.DaysMessage);
        }

        var count = await SessionLogPurge.Matching(db.PcSessions, days, clock.GetUtcNow()).CountAsync(cancellationToken);
        return Results.Ok(new SessionLogPurgePreviewResponse(count));
    }

    private static async Task<IResult> PurgeAsync(HttpContext http, AppDbContext db, TimeProvider clock, ILogger<SessionLogPurgeLog> logger)
    {
        var request = await AuthEndpoints.ReadBodyAsync<SessionLogPurgeRequest>(http.Request);
        if (request is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        if (!SessionLogPurge.IsAllowed(request.OlderThanDays))
        {
            return AuthEndpoints.BadRequest(SessionLogPurge.DaysMessage);
        }

        var deleted = await SessionLogPurge
            .Matching(db.PcSessions, request.OlderThanDays!.Value, clock.GetUtcNow())
            .ExecuteDeleteAsync(http.RequestAborted);
        logger.LogInformation(
            "Purged {Deleted} session logs older than {Days} days by {Administrator}.",
            deleted,
            request.OlderThanDays,
            ClientUpdates.ClientUpdateEndpoints.AdministratorName(http.User));
        return Results.Ok(new SessionLogPurgeResponse(deleted));
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] SessionLogListQuery query,
        HttpContext http,
        AppDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // Usernames and PC names must not sit in a browser or proxy cache.
        http.Response.Headers.CacheControl = "no-store";

        if (!SessionLogFilter.TryParse(query, out var filter, out var error))
        {
            return AuthEndpoints.BadRequest(error);
        }

        var now = clock.GetUtcNow();
        var matching = filter.ApplyTo(db.PcSessions.AsNoTracking(), clock.LocalTimeZone);
        var totalItems = await matching.CountAsync(cancellationToken);
        var page = SessionLogRules.ClampPage(filter.Page, totalItems, filter.PageSize);

        var sessions = await SessionLogFilter.NewestFirst(matching)
            .Skip((page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);

        return Results.Ok(new SessionLogPageResponse(
            sessions.Select(session => ToItem(session, now)).ToList(),
            totalItems,
            page,
            filter.PageSize));
    }

    private static async Task<IResult> ExportAsync(
        [AsParameters] SessionLogListQuery query,
        HttpContext http,
        AppDbContext db,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // Usernames and PC names must not sit in a browser or proxy cache.
        http.Response.Headers.CacheControl = "no-store";

        // An export takes every matching row, so the paging values are not checked.
        if (!SessionLogFilter.TryParse(query with { Page = null, PageSize = null }, out var filter, out var error))
        {
            return AuthEndpoints.BadRequest(error);
        }

        var now = clock.GetUtcNow();
        var zone = clock.LocalTimeZone;
        // One row more than the limit tells whether the export was cut short.
        var sessions = await SessionLogFilter.NewestFirst(filter.ApplyTo(db.PcSessions.AsNoTracking(), zone))
            .Take(MaxExportRows + 1)
            .ToListAsync(cancellationToken);
        var isTruncated = sessions.Count > MaxExportRows;

        http.Response.Headers.ContentDisposition = $"attachment; filename=\"{SessionLogCsv.FileNameFor(now, zone)}\"";
        if (isTruncated)
        {
            http.Response.Headers[ExportTruncatedHeader] = "true";
        }

        return Results.Bytes(
            SessionLogCsv.Write(sessions.Take(MaxExportRows), now, zone),
            "text/csv; charset=utf-8");
    }

    private static SessionLogItem ToItem(PcSession session, DateTimeOffset now) => new(
        session.Id,
        session.PcName,
        session.Username,
        SessionLogRules.SourceOf(session),
        session.StartedAtUtc,
        session.EndedAtUtc,
        SessionLogRules.DurationMinutes(session, now),
        session.EndReason);
}
