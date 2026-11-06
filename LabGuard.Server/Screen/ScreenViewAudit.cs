using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Screen;

// Records when a live view opened and when it ended. Only the metadata is written: never a frame, a view id or a token.
public sealed class ScreenViewAudit(IServiceScopeFactory scopes, ILogger<ScreenViewAudit> logger)
{
    public async Task<int> StartAsync(string adminUsername, string pcName, string mode, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = new ScreenViewEvent
        {
            AdminUsername = adminUsername,
            PcName = pcName,
            Mode = mode,
            StartedAtUtc = startedAt,
        };
        db.ScreenViewEvents.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return entry.Id;
    }

    // Never throws, so a failed write can't stop a view from closing. Only the exception type is logged.
    public async Task EndAsync(int id, DateTimeOffset endedAt)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.ScreenViewEvents
                .Where(e => e.Id == id && e.EndedAtUtc == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.EndedAtUtc, (DateTimeOffset?)endedAt));
        }
        catch (Exception exception)
        {
            try
            {
                logger.LogWarning("Couldn't record the end of screen view {ScreenViewEventId}: {ExceptionType}.", id, exception.GetType().Name);
            }
            catch (Exception)
            {
                // Logging is best effort too. A logger that fails must not turn a finished view into a crash.
            }
        }
    }
}
