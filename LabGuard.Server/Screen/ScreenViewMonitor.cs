using System.Net.WebSockets;
using LabGuard.Server.Data;
using LabGuard.Server.Pcs;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Screen;

// Ends live views whose administrator session has run out, or whose PC has gone offline or been removed.
// It also closes every view when the server stops, so no socket outlives the process.
public sealed class ScreenViewMonitor(
    ScreenStreamHub hub,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<ScreenViewMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The system timer, not the injected clock: checks keep running on schedule even when a test moves the clock.
        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Couldn't check the live screen views: {ExceptionType}.", exception.GetType().Name);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Runs before the web server stops, so its sockets are closed and it doesn't wait for them.
        await hub.CloseAllAsync(WebSocketCloseStatus.EndpointUnavailable, "The server is stopping.");
        await base.StopAsync(cancellationToken);
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var viewers = hub.Snapshot();
        foreach (var viewer in viewers.Where(viewer => now >= viewer.SessionDeadline))
        {
            await hub.CloseAsync(viewer, ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);
        }

        var watched = viewers.Where(viewer => now < viewer.SessionDeadline).ToArray();
        if (watched.Length == 0)
        {
            return;
        }

        var pcIds = watched.Select(viewer => viewer.PcId).Distinct().ToArray();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lastSeenByPc = await db.Pcs
            .AsNoTracking()
            .Where(p => pcIds.Contains(p.Id))
            .Select(p => new { p.Id, p.LastSeenUtc })
            .ToDictionaryAsync(p => p.Id, p => p.LastSeenUtc, cancellationToken);

        foreach (var viewer in watched)
        {
            // A PC that is missing was removed, and one that isn't online is offline. Both use the same close code.
            if (!lastSeenByPc.TryGetValue(viewer.PcId, out var lastSeen) || !PcEndpoints.IsOnline(lastSeen, now))
            {
                await hub.CloseAsync(viewer, ScreenProtocol.PcOfflineCloseStatus, "The PC is offline or was removed.");
            }
        }
    }
}
