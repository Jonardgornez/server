using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Screen;

// Records when a live view opened and when it ended. Only the metadata is written: never a frame, a view id or a token.
public sealed class ScreenViewAudit(IServiceScopeFactory scopes, ILogger<ScreenViewAudit> logger)
{
    private static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EndRetryDelay = TimeSpan.FromMilliseconds(250);
    private const int EndAttempts = 2;

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

    // Never throws, so a failed write can't stop a view from closing. Each attempt has a timeout, and one brief retry follows a failure.
    // Only the exception type is logged.
    public async Task EndAsync(int id, DateTimeOffset endedAt)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Sqlite doesn't stop a running statement when it is cancelled, so the wait itself is bounded too.
                await SetEndedAsync(id, endedAt).WaitAsync(EndTimeout);
                return;
            }
            catch (Exception exception)
            {
                if (attempt >= EndAttempts)
                {
                    LogEndFailure(id, exception);
                    return;
                }

                await Task.Delay(EndRetryDelay);
            }
        }
    }

    // No view survives a restart, so rows still open from before one end at the startup time.
    public async Task CloseOrphanedAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.ScreenViewEvents
            .Where(e => e.EndedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.EndedAtUtc, (DateTimeOffset?)startedAt), cancellationToken);
    }

    private async Task SetEndedAsync(int id, DateTimeOffset endedAt)
    {
        using var timeout = new CancellationTokenSource(EndTimeout);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.ScreenViewEvents
            .Where(e => e.Id == id && e.EndedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.EndedAtUtc, (DateTimeOffset?)endedAt), timeout.Token);
    }

    private void LogEndFailure(int id, Exception exception)
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
