using System.Text.Json;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;
using static LabGuard.Server.Tests.WallpaperTestSupport;

namespace LabGuard.Server.Tests;

// The heartbeat's side of the wallpaper. Each PC is told the hash of its own wallpaper and no other, and an offline PC gets
// its wallpaper on the first heartbeat after it assigned.
public class PcWallpaperHeartbeatEndpointTests
{
    private static string? WallpaperOf(JsonElement heartbeat) =>
        heartbeat.GetProperty("wallpaper").ValueKind == JsonValueKind.Null ? null : heartbeat.GetProperty("wallpaper").GetString();

    [Fact]
    public async Task Heartbeat_WallpaperIsNull_WhenNoneIsGiven()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);

        var body = await HeartbeatBodyAsync(client, pc);

        Assert.True(body.TryGetProperty("wallpaper", out var wallpaper));
        Assert.Equal(JsonValueKind.Null, wallpaper.ValueKind);
    }

    [Fact]
    public async Task Heartbeat_CarriesOnlyThisPcsHash()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var firstClient = app.CreatePcClient(OtherPcAddress);
        using var secondClient = app.CreatePcClient("203.0.113.6");
        var first = await AddPcAsync(app, "PC-01");
        var second = await AddPcAsync(app, "PC-02");
        var unassigned = await AddPcAsync(app, "PC-03");
        var firstBytes = BackgroundTestImages.Png(16, 16);
        var secondBytes = BackgroundTestImages.Png(32, 32);
        await AssignAsync(admin, await UploadIdAsync(admin, firstBytes), first.Id);
        await AssignAsync(admin, await UploadIdAsync(admin, secondBytes), second.Id);

        Assert.Equal(Sha256Of(firstBytes), WallpaperOf(await HeartbeatBodyAsync(firstClient, first)));
        Assert.Equal(Sha256Of(secondBytes), WallpaperOf(await HeartbeatBodyAsync(secondClient, second)));
        Assert.Null(WallpaperOf(await HeartbeatBodyAsync(firstClient, unassigned)));
    }

    [Fact]
    public async Task Heartbeat_ForAnOfflinePc_CarriesTheWallpaperOnItsNextHeartbeat()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app, "PC-01", seenAgo: TimeSpan.FromMinutes(5));
        var bytes = BackgroundTestImages.Png(16, 16);
        await AssignAsync(admin, await UploadIdAsync(admin, bytes), pc.Id);

        var body = await HeartbeatBodyAsync(client, pc);

        Assert.Equal(Sha256Of(bytes), WallpaperOf(body));
    }

    [Fact]
    public async Task Heartbeat_AfterTheWallpaperIsRemoved_IsNull()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await AssignAsync(admin, await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16)), pc.Id);
        await AssignAsync(admin, null, pc.Id);

        var body = await HeartbeatBodyAsync(client, pc);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("wallpaper").ValueKind);
    }
}
