using System.Net.Http.Json;
using System.Text.Json;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The heartbeat's side of the internet block. Every heartbeat tells the PC what the administrator wants, and the PC reports back
// what it applied. A report of null, from an older client, leaves the stored report as it was.
public class PcInternetHeartbeatEndpointTests
{
    private static Task<HttpResponseMessage> HeartbeatWithAsync(HttpClient client, TestPc pc, object body) =>
        PostAsync(client, "/api/client/heartbeat", pc.Token, JsonContent.Create(body));

    private static bool InternetBlockedOf(JsonElement heartbeat) => heartbeat.GetProperty("internetBlocked").GetBoolean();

    [Fact]
    public async Task Heartbeat_ReportsInternetBlockedFalse_ByDefault()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);

        var body = await HeartbeatBodyAsync(client, pc);

        Assert.False(InternetBlockedOf(body));
    }

    [Fact]
    public async Task Heartbeat_ReportsInternetBlocked_WhenTheAdministratorBlocksIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await admin.PostAsJsonAsync("/api/pcs/internet", new { pcIds = new[] { pc.Id }, blocked = true });

        var body = await HeartbeatBodyAsync(client, pc);

        Assert.True(InternetBlockedOf(body));
    }

    [Fact]
    public async Task Heartbeat_ReportingAnApplied_DoesNotChangeWhatTheAdministratorWants()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);

        var body = await JsonOf(await HeartbeatWithAsync(client, pc, new { internetBlockApplied = true }));

        Assert.False(InternetBlockedOf(body));
        Assert.False((await StoredPcAsync(app, pc.Id)).InternetBlocked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Heartbeat_StoresTheReportedInternetBlockApplied(bool applied)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);

        await HeartbeatWithAsync(client, pc, new { internetBlockApplied = applied });

        Assert.Equal(applied, (await StoredPcAsync(app, pc.Id)).InternetBlockApplied);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Heartbeat_ALaterReport_ReplacesTheEarlierOne(bool first, bool second)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatWithAsync(client, pc, new { internetBlockApplied = first });

        await HeartbeatWithAsync(client, pc, new { internetBlockApplied = second });

        Assert.Equal(second, (await StoredPcAsync(app, pc.Id)).InternetBlockApplied);
    }

    [Fact]
    public async Task Heartbeat_WithANullReport_LeavesTheStoredReportAsItWas()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatWithAsync(client, pc, new { internetBlockApplied = true });

        await HeartbeatWithAsync(client, pc, new { internetBlockApplied = (bool?)null });

        Assert.True((await StoredPcAsync(app, pc.Id)).InternetBlockApplied);
    }

    [Fact]
    public async Task Heartbeat_WithoutAnyBody_LeavesTheStoredReportAsItWas()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await HeartbeatWithAsync(client, pc, new { internetBlockApplied = false });

        await HeartbeatAsync(client, pc);

        Assert.False((await StoredPcAsync(app, pc.Id)).InternetBlockApplied);
    }

    [Fact]
    public async Task Heartbeat_FromAnOlderClient_KeepsTheReportNull()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);

        await HeartbeatWithAsync(client, pc, new { internetBlockApplied = (bool?)null });

        Assert.Null((await StoredPcAsync(app, pc.Id)).InternetBlockApplied);
    }
}
