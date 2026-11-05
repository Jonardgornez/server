using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Pcs;

// Admin lock and unlock of a PC's session. Every route requires a JSON body, so a cross-origin HTML form can't trigger it.
public static partial class PcEndpoints
{
    private static async Task<IResult> LockAsync(int id, HttpContext http, AppDbContext db, TimeProvider clock)
    {
        if (await AuthEndpoints.ReadBodyAsync<PcAdminActionRequest>(http.Request) is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        var ct = http.RequestAborted;
        if (!await db.Pcs.AnyAsync(p => p.Id == id, ct))
        {
            return PcNotFound();
        }

        // A session that already ran out is recorded as time-expired; only the rest is ended as an admin lock.
        var now = clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SessionLifecycle.EndExpiredForPcAsync(db, id, now, ct);
        await SessionLifecycle.EndForPcAsync(db, id, SessionLifecycle.AdminLock, now, ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static IResult PcNotFound() =>
        Results.Json(new ErrorResponse("PC not found."), statusCode: StatusCodes.Status404NotFound);
}
