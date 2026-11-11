using System.Net;
using System.Net.Http.Json;
using System.Text;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The administrator turns "always awake" on or off for PCs. Turning it on changes only PCs online right now; offline and
// unknown PCs are skipped and left out of the count. Turning it off changes every named PC, offline ones too.
public class PcKeepAwakeEndpointTests
{
    private const string KeepAwakePath = "/api/pcs/keep-awake";

    private static Task<HttpResponseMessage> SetKeepAwakeAsync(HttpClient client, bool keepAwake, params int[] ids) =>
        client.PostAsJsonAsync(KeepAwakePath, new { ids, keepAwake });

    private static async Task<bool> StoredKeepAwakeAsync(TestApp app, int pcId) =>
        (await StoredPcAsync(app, pcId)).KeepAwake;

    // Every admin route refuses anonymous callers, the PC port, and any request without a JSON body.

    [Fact]
    public async Task KeepAwakeRoute_WithoutCookie_Returns401()
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();
        var pc = await AddPcAsync(app);

        var response = await SetKeepAwakeAsync(browser, true, pc.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(await StoredKeepAwakeAsync(app, pc.Id));
    }

    [Fact]
    public async Task KeepAwakeRoute_FromThePcPort_Returns404()
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);

        var response = await SetKeepAwakeAsync(pcClient, true, pc.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(await StoredKeepAwakeAsync(app, pc.Id));
    }

    [Theory]
    [InlineData("text/plain", "{}")]
    [InlineData("application/x-www-form-urlencoded", "")]
    [InlineData("application/json", "")]
    [InlineData("application/json", "null")]
    [InlineData("application/json", "{not json")]
    public async Task KeepAwakeRoute_WithoutAJsonBody_Returns400AndChangesNothing(string contentType, string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        using var content = new StringContent(body, Encoding.UTF8, contentType);
        var response = await admin.PostAsync(KeepAwakePath, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.False(await StoredKeepAwakeAsync(app, pc.Id));
    }

    // Each body is refused: a missing, null or empty list, a wrong type, or a missing or null keepAwake.
    // A body that leaves out keepAwake is never read as off.
    [Theory]
    [InlineData("""{"keepAwake":true}""")]
    [InlineData("""{"ids":null,"keepAwake":true}""")]
    [InlineData("""{"ids":[],"keepAwake":true}""")]
    [InlineData("""{"ids":"one","keepAwake":true}""")]
    [InlineData("""{"ids":[1]}""")]
    [InlineData("""{"ids":[1],"keepAwake":null}""")]
    [InlineData("""{"ids":[1],"keepAwake":"yes"}""")]
    public async Task KeepAwakeRoute_InvalidBody_Returns400AndChangesNothing(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var response = await admin.PostAsync(KeepAwakePath, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.False(await StoredKeepAwakeAsync(app, pc.Id));
    }

    [Fact]
    public async Task KeepAwakeRoute_MoreThanTheLimit_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        // One over the server's limit of 1000 PCs per request.
        var ids = Enumerable.Range(1, 1001).ToArray();

        var response = await SetKeepAwakeAsync(admin, true, ids);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Setting

    [Fact]
    public async Task KeepAwake_TurnsItOnForOnlinePcs_AndSkipsOfflineAndUnknownOnes()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var first = await AddPcAsync(app, "PC-01");
        var second = await AddPcAsync(app, "PC-02");
        var offline = await AddPcAsync(app, "PC-03", seenAgo: TimeSpan.FromMinutes(5));

        var response = await SetKeepAwakeAsync(admin, true, first.Id, second.Id, offline.Id, 999);

        Assert.Equal(2, await AffectedAsync(response));
        Assert.True(await StoredKeepAwakeAsync(app, first.Id));
        Assert.True(await StoredKeepAwakeAsync(app, second.Id));
        Assert.False(await StoredKeepAwakeAsync(app, offline.Id));
    }

    [Fact]
    public async Task KeepAwake_TurningItOff_ClearsIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        Assert.Equal(1, await AffectedAsync(await SetKeepAwakeAsync(admin, true, pc.Id)));

        var response = await SetKeepAwakeAsync(admin, false, pc.Id);

        Assert.Equal(1, await AffectedAsync(response));
        Assert.False(await StoredKeepAwakeAsync(app, pc.Id));
    }

    [Fact]
    public async Task KeepAwake_TurningItOff_AlsoReachesOfflinePcs_ButSkipsUnknownOnes()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        Assert.Equal(1, await AffectedAsync(await SetKeepAwakeAsync(admin, true, pc.Id)));
        app.Time.Advance(TimeSpan.FromMinutes(5));

        var response = await SetKeepAwakeAsync(admin, false, pc.Id, 999);

        Assert.Equal(1, await AffectedAsync(response));
        Assert.False(await StoredKeepAwakeAsync(app, pc.Id));
    }

    // The online window is the same as the PC list's: a PC seen just inside it counts, one seen exactly at its edge doesn't.
    [Fact]
    public async Task KeepAwake_UsesTheSameOnlineWindowAsThePcList()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var inside = await AddPcAsync(app, "PC-01", seenAgo: TimeSpan.FromSeconds(14));
        var edge = await AddPcAsync(app, "PC-02", seenAgo: TimeSpan.FromSeconds(15));

        var response = await SetKeepAwakeAsync(admin, true, inside.Id, edge.Id);

        Assert.Equal(1, await AffectedAsync(response));
        Assert.True((await ListEntryAsync(admin, "PC-01")).GetProperty("isOnline").GetBoolean());
        Assert.False((await ListEntryAsync(admin, "PC-02")).GetProperty("isOnline").GetBoolean());
        Assert.True(await StoredKeepAwakeAsync(app, inside.Id));
        Assert.False(await StoredKeepAwakeAsync(app, edge.Id));
    }

    // A PC last seen in the future (the server clock moved back) counts as offline.
    [Fact]
    public async Task KeepAwake_PcSeenInTheFuture_IsSkipped()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, seenAgo: TimeSpan.FromSeconds(-5));

        var response = await SetKeepAwakeAsync(admin, true, pc.Id);

        Assert.Equal(0, await AffectedAsync(response));
        Assert.False(await StoredKeepAwakeAsync(app, pc.Id));
    }

    // PC list

    [Fact]
    public async Task List_ShowsKeepAwake()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-01");
        await AddPcAsync(app, "PC-02");
        await SetKeepAwakeAsync(admin, true, pc.Id);

        Assert.True((await ListEntryAsync(admin, "PC-01")).GetProperty("keepAwake").GetBoolean());
        Assert.False((await ListEntryAsync(admin, "PC-02")).GetProperty("keepAwake").GetBoolean());
    }
}
