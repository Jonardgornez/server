using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Screen;

// Records each period of remote control: when it started and when it ended. Only the metadata is written, never input or a frame.
public sealed class ScreenControlAudit(IServiceScopeFactory scopes, ILogger<ScreenControlAudit> logger)
{
    private static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EndRetryDelay = TimeSpan.FromMilliseconds(250);
    private const int EndAttempts = 2;

    public async Task<int> StartAsync(string adminUserName, string pcName, string mode, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = new ScreenControlEvent
        {
            AdminUserName = adminUserName,
            PcName = pcName,
            Mode = mode,
            StartedUtc = startedAt,
        };
        db.ScreenControlEvents.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return entry.Id;
    }

    // Never throws, so a failed write can't stop control from ending. Each attempt has a timeout, and one brief retry follows a failure.
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

    // No control survives a restart, so rows still open from before one end at the startup time.
    public async Task CloseOrphanedAsync(DateTimeOffset closedAt, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.ScreenControlEvents
            .Where(e => e.EndedUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.EndedUtc, (DateTimeOffset?)closedAt), cancellationToken);
    }

    private async Task SetEndedAsync(int id, DateTimeOffset endedAt)
    {
        using var timeout = new CancellationTokenSource(EndTimeout);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.ScreenControlEvents
            .Where(e => e.Id == id && e.EndedUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.EndedUtc, (DateTimeOffset?)endedAt), timeout.Token);
    }

    private void LogEndFailure(int id, Exception exception)
    {
        try
        {
            logger.LogWarning("Couldn't record the end of screen control {ScreenControlEventId}: {ExceptionType}.", id, exception.GetType().Name);
        }
        catch (Exception)
        {
            // Logging is best effort too. A logger that fails must not turn an ended control period into a crash.
        }
    }
}
