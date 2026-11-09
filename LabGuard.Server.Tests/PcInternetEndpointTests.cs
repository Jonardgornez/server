using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Pcs;
using Microsoft.EntityFrameworkCore;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The administrator's internet block on PCs. The request only records the wanted state. The PC applies it on its next heartbeat,
// so offline PCs are recorded too. The server's own PC is never blocked, and the applied state is only written by the heartbeat.
public class PcInternetEndpointTests
{
    private const string InternetPath = "/api/pcs/internet";

    private static Task<HttpResponseMessage> SetInternetBlockAsync(HttpClient admin, bool blocked, params int[] pcIds) =>
        admin.PostAsJsonAsync(InternetPath, new { pcIds, blocked });

    private static string ResultAt(JsonElement body, int index) =>
        body.GetProperty("results")[index].GetProperty("result").GetString()!;

    // Every admin route refuses anonymous callers, the PC port, and any request without a JSON body.

    [Fact]
    public async Task InternetRoute_WithoutCookie_Returns401()
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();

        var response = await browser.PostAsJsonAsync(InternetPath, new { pcIds = new[] { 1 }, blocked = true });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InternetRoute_FromThePcPort_Returns404()
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);

        var response = await pcClient.PostAsJsonAsync(InternetPath, new { pcIds = new[] { pc.Id }, blocked = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False((await StoredPcAsync(app, pc.Id)).InternetBlocked);
    }

    [Theory]
    [InlineData("text/plain", "{}")]
    [InlineData("application/x-www-form-urlencoded", "")]
    [InlineData("application/json", "")]
    [InlineData("application/json", "null")]
    [InlineData("application/json", "{not json")]
    public async Task InternetRoute_WithoutAJsonBody_Returns400AndChangesNothing(string contentType, string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        using var content = new StringContent(body, Encoding.UTF8, contentType);
        var response = await admin.PostAsync(InternetPath, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.False((await StoredPcAsync(app, pc.Id)).InternetBlocked);
    }

    // Each body is a request the rules refuse: a missing or empty list, a non-positive or repeated id, or no blocked flag.
    [Theory]
    [InlineData("""{"blocked":true}""")]
    [InlineData("""{"pcIds":null,"blocked":true}""")]
    [InlineData("""{"pcIds":[],"blocked":true}""")]
    [InlineData("""{"pcIds":[0],"blocked":true}""")]
    [InlineData("""{"pcIds":[-1],"blocked":true}""")]
    [InlineData("""{"pcIds":[1,1],"blocked":true}""")]
    [InlineData("""{"pcIds":[1]}""")]
    [InlineData("""{"pcIds":[1],"blocked":null}""")]
    public async Task InternetRoute_InvalidRequest_Returns400AndChangesNothing(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var response = await admin.PostAsync(InternetPath, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.False((await StoredPcAsync(app, pc.Id)).InternetBlocked);
    }

    [Fact]
    public async Task InternetRoute_MoreThanTheLimit_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var ids = Enumerable.Range(1, PcEndpoints.MaxInternetBlockPcIds + 1).ToArray();

        var response = await SetInternetBlockAsync(admin, true, ids);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InternetRoute_ExactlyTheLimit_IsAccepted()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var ids = Enumerable.Range(1, PcEndpoints.MaxInternetBlockPcIds).ToArray();

        var response = await SetInternetBlockAsync(admin, true, ids);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = (await JsonOf(response)).GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(PcEndpoints.MaxInternetBlockPcIds, results.Length);
        Assert.All(results, result => Assert.Equal("not-found", result.GetProperty("result").GetString()));
    }

    // Results

    [Fact]
    public async Task InternetRoute_Ok_SetsTheBlockAndReportsThePcName()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-01");

        var response = await SetInternetBlockAsync(admin, true, pc.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await JsonOf(response)).GetProperty("results")[0];
        Assert.Equal(pc.Id, result.GetProperty("pcId").GetInt32());
        Assert.Equal("PC-01", result.GetProperty("pcName").GetString());
        Assert.Equal("ok", result.GetProperty("result").GetString());
        Assert.True((await StoredPcAsync(app, pc.Id)).InternetBlocked);
    }

    [Fact]
    public async Task InternetRoute_Unblock_ClearsTheBlock()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        await SetInternetBlockAsync(admin, true, pc.Id);

        var response = await SetInternetBlockAsync(admin, false, pc.Id);

        Assert.Equal("ok", ResultAt(await JsonOf(response), 0));
        Assert.False((await StoredPcAsync(app, pc.Id)).InternetBlocked);
    }

    [Fact]
    public async Task InternetRoute_RepeatedCall_IsIdempotent()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var first = await JsonOf(await SetInternetBlockAsync(admin, true, pc.Id));
        var second = await JsonOf(await SetInternetBlockAsync(admin, true, pc.Id));

        Assert.Equal("ok", ResultAt(first, 0));
        Assert.Equal("ok", ResultAt(second, 0));
        Assert.True((await StoredPcAsync(app, pc.Id)).InternetBlocked);
    }

    [Fact]
    public async Task InternetRoute_OfflinePc_IsStillRecorded()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, seenAgo: TimeSpan.FromMinutes(5));

        var response = await SetInternetBlockAsync(admin, true, pc.Id);

        Assert.Equal("ok", ResultAt(await JsonOf(response), 0));
        Assert.True((await StoredPcAsync(app, pc.Id)).InternetBlocked);
    }

    [Fact]
    public async Task InternetRoute_UnknownPc_IsNotFound_WithNoName()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await SetInternetBlockAsync(admin, true, 999);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await JsonOf(response)).GetProperty("results")[0];
        Assert.Equal(999, result.GetProperty("pcId").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("pcName").ValueKind);
        Assert.Equal("not-found", result.GetProperty("result").GetString());
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData(TestApp.MachineLanAddress)]
    public async Task InternetRoute_ServerPc_IsRefusedAndNeverBlocked(string address)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(address);
        var pc = await AddPcAsync(app, "PC-01");
        await HeartbeatAsync(client, pc);

        var response = await SetInternetBlockAsync(admin, true, pc.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await JsonOf(response)).GetProperty("results")[0];
        Assert.Equal("server-pc", result.GetProperty("result").GetString());
        Assert.Equal("PC-01", result.GetProperty("pcName").GetString());
        Assert.False((await StoredPcAsync(app, pc.Id)).InternetBlocked);
    }

    [Fact]
    public async Task InternetRoute_MixedPcs_ReportsEachInRequestOrder_AndChangesOnlyTheOk()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var serverClient = app.CreatePcClient("127.0.0.1");
        using var otherClient = app.CreatePcClient(OtherPcAddress);
        var serverPc = await AddPcAsync(app, "PC-SERVER");
        var otherPc = await AddPcAsync(app, "PC-02");
        await HeartbeatAsync(serverClient, serverPc);
        await HeartbeatAsync(otherClient, otherPc);

        var response = await SetInternetBlockAsync(admin, true, otherPc.Id, 999, serverPc.Id);

        var results = (await JsonOf(response)).GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(new[] { otherPc.Id, 999, serverPc.Id }, results.Select(r => r.GetProperty("pcId").GetInt32()).ToArray());
        Assert.Equal(new[] { "ok", "not-found", "server-pc" }, results.Select(r => r.GetProperty("result").GetString()).ToArray());
        Assert.True((await StoredPcAsync(app, otherPc.Id)).InternetBlocked);
        Assert.False((await StoredPcAsync(app, serverPc.Id)).InternetBlocked);
    }

    [Fact]
    public async Task InternetRoute_DoesNotChangeTheAppliedState()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        await app.WithDbAsync(db => db.Pcs
            .Where(p => p.Id == pc.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.InternetBlockApplied, (bool?)false)));

        await SetInternetBlockAsync(admin, true, pc.Id);

        Assert.False((await StoredPcAsync(app, pc.Id)).InternetBlockApplied);
    }

    [Fact]
    public async Task InternetRoute_WhenTheAddressesCannotBeRead_Returns409AndChangesNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        app.LocalAddresses.Fail = true;

        var response = await SetInternetBlockAsync(admin, true, pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(AddressUnreadableMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.False((await StoredPcAsync(app, pc.Id)).InternetBlocked);
    }

    // PC list

    [Fact]
    public async Task List_InternetBlockApplied_IsNullUntilThePcReports()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await AddPcAsync(app, "PC-01");

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.False(entry.GetProperty("internetBlocked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("internetBlockApplied").ValueKind);
    }

    [Fact]
    public async Task List_ShowsTheWantedBlockAndWhatThePcApplied()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app, "PC-01");
        await SetInternetBlockAsync(admin, true, pc.Id);
        await PostAsync(client, "/api/client/heartbeat", pc.Token, JsonContent.Create(new { internetBlockApplied = false }));

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.True(entry.GetProperty("internetBlocked").GetBoolean());
        Assert.False(entry.GetProperty("internetBlockApplied").GetBoolean());
    }
}
