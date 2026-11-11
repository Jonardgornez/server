using System.Text.Json;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// The update the service reported: its version, state and error, each null when it wasn't readable.
public record PcServiceUpdate(string? Version, string? State, string? Error);

// The latest service report of a PC. Update is null when the report carried no update. Events are in the order the service sent them.
public record PcDiagnosticsResponse(
    DateTimeOffset ReceivedAt,
    string? ServiceVersion,
    bool ClientRunning,
    string? ClientVersion,
    bool UserSignedIn,
    PcServiceUpdate? Update,
    ServiceReportEvent[] Events);

public static partial class PcEndpoints
{
    private static async Task<IResult> DiagnosticsAsync(int id, HttpContext http, AppDbContext db, CancellationToken cancellationToken)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!await db.Pcs.AnyAsync(p => p.Id == id, cancellationToken))
        {
            return Results.Json(new ErrorResponse("PC not found."), statusCode: StatusCodes.Status404NotFound);
        }

        var report = await db.PcServiceReports
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.PcId == id, cancellationToken);
        if (report is null)
        {
            return Results.Json(new ErrorResponse("No report from this PC yet."), statusCode: StatusCodes.Status404NotFound);
        }

        var hasUpdate = report.UpdateVersion is not null || report.UpdateState is not null || report.UpdateError is not null;
        return Results.Ok(new PcDiagnosticsResponse(
            report.ReceivedAtUtc,
            report.ServiceVersion,
            report.ClientRunning,
            report.ClientVersion,
            report.UserSignedIn,
            hasUpdate ? new PcServiceUpdate(report.UpdateVersion, report.UpdateState, report.UpdateError) : null,
            ReadEvents(report.EventsJson)));
    }

    // The events are written by the server, but a damaged row shows no events rather than failing the whole view.
    private static ServiceReportEvent[] ReadEvents(string eventsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<ServiceReportEvent?[]>(eventsJson)?.OfType<ServiceReportEvent>().ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
