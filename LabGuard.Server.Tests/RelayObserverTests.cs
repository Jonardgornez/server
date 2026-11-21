using LabGuard.Server.Screen;
using Microsoft.Extensions.Logging;

namespace LabGuard.Server.Tests;

public class RelayObserverTests
{
    [Fact]
    public async Task ObserveAsync_LogsAFailedRelayByType_AndDoesNotThrow()
    {
        using var logs = new CapturingLoggerProvider();
        var logger = logs.CreateLogger("test");

        await RelayObserver.ObserveAsync(Task.FromException(new InvalidOperationException("frame bytes")), logger);

        var entry = Assert.Single(logs.Entries);
        Assert.Contains("InvalidOperationException", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("frame bytes", entry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObserveAsync_LogsNothingForARelayThatSucceeds()
    {
        using var logs = new CapturingLoggerProvider();

        await RelayObserver.ObserveAsync(Task.CompletedTask, logs.CreateLogger("test"));

        Assert.Empty(logs.Entries);
    }
}
