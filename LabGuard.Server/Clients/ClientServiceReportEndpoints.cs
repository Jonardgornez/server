using System.Text.Json;
using System.Text.Json.Serialization;
using LabGuard.Server.Auth;
using LabGuard.Server.ClientUpdates;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Clients;

// Sent by the background service of a PC every minute. Each part is read leniently by ServiceReportReader, so a wrongly typed part never refuses it.
public record ServiceReportRequest(
    JsonElement? ServiceVersion,
    JsonElement? ClientRunning,
    JsonElement? ClientVersion,
    JsonElement? UserSignedIn,
    JsonElement? Update,
    JsonElement? Events);

// ClientUpdate is the offer the heartbeat would make: the stored installer, while the PC's pending update targets it and it is still behind.
// Left out otherwise, so the answer is {} when there is nothing to offer.
public record ServiceReportResponse([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClientUpdateOffer? ClientUpdate);

// The service report is a second source of the PC's version and update, next to the heartbeat. Only the latest report of a PC is kept.
// It never changes LastSeenUtc, which stays with the client heartbeat.
public static partial class ClientEndpoints
{
    private static async Task<IResult> ServiceReportAsync(HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (ReadBearerToken(http.Request) is not { } token)
        {
            return NotRegistered();
        }

        var ct = http.RequestAborted;
        if (await FindPcIdAsync(db, token, ct) is not { } pcId)
        {
            return NotRegistered();
        }

        var request = await AuthEndpoints.ReadBodyAsync<ServiceReportRequest>(http.Request);
        if (request is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var now = clock.GetUtcNow();
        var values = ServiceReportReader.Read(request);
        await SaveServiceReportAsync(db, pcId, values, now, ct);

        var progress = values.Update is { Version: { } version, State: { } state }
            ? new ClientUpdateReport(version, state, values.Update.Error)
            : null;
        var clientUpdate = await ClientUpdateHeartbeat.HandleServiceReportAsync(db, pcId, values.ClientVersion, progress, now, ct);
        return Results.Ok(new ServiceReportResponse(clientUpdate));
    }

    // Keeps one row per PC: the first report adds it, later reports replace it. The row goes with the PC, so a report never outlives its PC.
    private static async Task SaveServiceReportAsync(AppDbContext db, int pcId, ServiceReportValues values, DateTimeOffset now, CancellationToken ct)
    {
        var report = await db.PcServiceReports.FirstOrDefaultAsync(r => r.PcId == pcId, ct);
        if (report is null)
        {
            report = new PcServiceReport { PcId = pcId };
            db.PcServiceReports.Add(report);
        }

        report.ReceivedAtUtc = now;
        report.ServiceVersion = values.ServiceVersion;
        report.ClientRunning = values.ClientRunning;
        report.ClientVersion = values.ClientVersion?.ToString();
        report.UserSignedIn = values.UserSignedIn;
        report.UpdateVersion = values.Update?.Version;
        report.UpdateState = values.Update?.State;
        report.UpdateError = values.Update?.Error;
        report.EventsJson = JsonSerializer.Serialize(values.Events);
        await db.SaveChangesAsync(ct);
    }
}
