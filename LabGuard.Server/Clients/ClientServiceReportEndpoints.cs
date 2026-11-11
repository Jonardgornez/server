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
        var values = ServiceReportReader.Read(request, now);
        // The PC can be removed while its report is on the way. The report is then not saved, and answered like any unknown PC.
        if (!await SaveServiceReportAsync(db, pcId, values, now, ct))
        {
            return NotRegistered();
        }

        var clientUpdate = await ClientUpdateHeartbeat.HandleServiceReportAsync(db, pcId, values.ClientVersion, HeartbeatProgress(values.Update), now, ct);
        return Results.Ok(new ServiceReportResponse(clientUpdate));
    }

    // Only the update states the heartbeat also reports are recorded on the PC. The service's own pending and waiting stay in the
    // stored report: the client reports downloading for the same phase, so recording them would flip the PC's state every minute.
    private static ClientUpdateReport? HeartbeatProgress(ServiceReportUpdate? update) =>
        update is { Version: { } version, State: { } state } && ClientUpdateHeartbeat.IsReportedState(state)
            ? new ClientUpdateReport(version, state, update.Error)
            : null;

    // Keeps one row per PC: the first report adds it, later reports replace it. The row goes with the PC, so a report never outlives its PC.
    // One statement, so two first reports at once never clash on the key, and a PC removed meanwhile gets no row. False when the PC is gone.
    private static async Task<bool> SaveServiceReportAsync(AppDbContext db, int pcId, ServiceReportValues values, DateTimeOffset now, CancellationToken ct)
    {
        var receivedAt = now.ToUnixTimeMilliseconds();
        var clientVersion = values.ClientVersion?.ToString();
        var eventsJson = JsonSerializer.Serialize(values.Events);
        var saved = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "PcServiceReports"
                ("PcId", "ReceivedAtUtc", "ServiceVersion", "ClientRunning", "ClientVersion", "UserSignedIn", "UpdateVersion", "UpdateState", "UpdateError", "EventsJson")
            SELECT {pcId}, {receivedAt}, {values.ServiceVersion}, {values.ClientRunning}, {clientVersion}, {values.UserSignedIn},
                {values.Update?.Version}, {values.Update?.State}, {values.Update?.Error}, {eventsJson}
            WHERE EXISTS (SELECT 1 FROM "Pcs" WHERE "Id" = {pcId})
            ON CONFLICT ("PcId") DO UPDATE SET
                "ReceivedAtUtc" = excluded."ReceivedAtUtc",
                "ServiceVersion" = excluded."ServiceVersion",
                "ClientRunning" = excluded."ClientRunning",
                "ClientVersion" = excluded."ClientVersion",
                "UserSignedIn" = excluded."UserSignedIn",
                "UpdateVersion" = excluded."UpdateVersion",
                "UpdateState" = excluded."UpdateState",
                "UpdateError" = excluded."UpdateError",
                "EventsJson" = excluded."EventsJson"
            """, ct);
        return saved > 0;
    }
}
