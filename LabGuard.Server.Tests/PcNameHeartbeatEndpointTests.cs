using System.Net;
using System.Net.Http.Json;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The heartbeat's side of the PC name. Every heartbeat tells the PC its current name, so it shows a rename by the administrator.
public class PcNameHeartbeatEndpointTests
{
    [Fact]
    public async Task Heartbeat_CarriesThePcsName()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app, "PC-3");

        var body = await HeartbeatBodyAsync(client, pc);

        Assert.Equal("PC-3", body.GetProperty("pcName").GetString());
    }

    [Fact]
    public async Task Heartbeat_AfterARename_CarriesTheNewName()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app, "PC-1");
        var rename = await admin.PutAsJsonAsync($"/api/pcs/{pc.Id}/name", new { number = 9 });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

        var body = await HeartbeatBodyAsync(client, pc);

        Assert.Equal("PC-9", body.GetProperty("pcName").GetString());
    }
}
