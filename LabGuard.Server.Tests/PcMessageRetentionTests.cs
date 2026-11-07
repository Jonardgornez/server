using LabGuard.Server.Pcs;
using static LabGuard.Server.Tests.PcMessageEndpointTests;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

public class PcMessageRetentionTests
{
    [Fact]
    public async Task DeleteOlderThan_RemovesMessagesOlderThanNinetyDays_AndKeepsTheRest()
    {
        using var app = new TestApp();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();
        await SeedMessageAsync(app, pc, createdUtc: now - PcMessageRetention.KeepFor - TimeSpan.FromSeconds(1));
        var exactlyKept = await SeedMessageAsync(app, pc, createdUtc: now - PcMessageRetention.KeepFor);
        var recent = await SeedMessageAsync(app, pc, createdUtc: now - TimeSpan.FromDays(3));

        var removed = await app.WithDbAsync(db => PcMessageRetention.DeleteOlderThanAsync(db, now, CancellationToken.None));

        Assert.Equal(1, removed);
        Assert.Equal(new[] { exactlyKept, recent }, (await StoredMessagesAsync(app)).Select(m => m.Id).ToArray());
    }
}
