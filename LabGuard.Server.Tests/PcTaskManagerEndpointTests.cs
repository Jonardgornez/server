using System.Net;
using System.Net.Http.Json;
using System.Text;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The administrator blocks or allows Task Manager on PCs under Restrictions. Every named PC changes, offline ones too,
// since the PC's service picks it up from its next report. Unknown ids are left out of the count.
public class PcTaskManagerEndpointTests
{
    private const string TaskManagerPath = "/api/pcs/task-manager";

    private static Task<HttpResponseMessage> SetAsync(HttpClient client, bool disabled, params int[] ids) =>
        client.PostAsJsonAsync(TaskManagerPath, new { ids, disabled });

    private static async Task<bool> StoredAsync(TestApp app, int pcId) =>
        (await StoredPcAsync(app, pcId)).TaskManagerDisabled;

    [Fact]
    public async Task Route_WithoutCookie_Returns401()
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();
        var pc = await AddPcAsync(app);

        var response = await SetAsync(browser, true, pc.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(await StoredAsync(app, pc.Id));
    }

    [Fact]
    public async Task Route_FromThePcPort_Returns404()
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);

        var response = await SetAsync(pcClient, true, pc.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(await StoredAsync(app, pc.Id));
    }

    [Theory]
    [InlineData("text/plain", "{}")]
    [InlineData("application/x-www-form-urlencoded", "")]
    [InlineData("application/json", "")]
    [InlineData("application/json", "null")]
    [InlineData("application/json", "{not json")]
    public async Task Route_WithoutAJsonBody_Returns400AndChangesNothing(string contentType, string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        using var content = new StringContent(body, Encoding.UTF8, contentType);
        var response = await admin.PostAsync(TaskManagerPath, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.False(await StoredAsync(app, pc.Id));
    }

    // A body that leaves out disabled is never read as allowed.
    [Theory]
    [InlineData("""{"disabled":true}""")]
    [InlineData("""{"ids":null,"disabled":true}""")]
    [InlineData("""{"ids":[],"disabled":true}""")]
    [InlineData("""{"ids":"one","disabled":true}""")]
    [InlineData("""{"ids":[1]}""")]
    [InlineData("""{"ids":[1],"disabled":null}""")]
    [InlineData("""{"ids":[1],"disabled":"yes"}""")]
    public async Task Route_InvalidBody_Returns400AndChangesNothing(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var response = await admin.PostAsync(TaskManagerPath, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.False(await StoredAsync(app, pc.Id));
    }

    [Fact]
    public async Task Route_MoreThanTheLimit_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await SetAsync(admin, true, Enumerable.Range(1, 1001).ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Disable_ReachesOnlineAndOfflinePcs_AndSkipsUnknownOnes()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var online = await AddPcAsync(app, "PC-01");
        var offline = await AddPcAsync(app, "PC-02", seenAgo: TimeSpan.FromHours(2));
        var untouched = await AddPcAsync(app, "PC-03");

        var response = await SetAsync(admin, true, online.Id, offline.Id, 999);

        Assert.Equal(2, await AffectedAsync(response));
        Assert.True(await StoredAsync(app, online.Id));
        Assert.True(await StoredAsync(app, offline.Id));
        Assert.False(await StoredAsync(app, untouched.Id));
    }

    [Fact]
    public async Task Allow_ClearsIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        await SetAsync(admin, true, pc.Id);

        var response = await SetAsync(admin, false, pc.Id);

        Assert.Equal(1, await AffectedAsync(response));
        Assert.False(await StoredAsync(app, pc.Id));
    }

    [Fact]
    public async Task List_ShowsTaskManagerDisabled()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-01");
        await AddPcAsync(app, "PC-02");
        await SetAsync(admin, true, pc.Id);

        Assert.True((await ListEntryAsync(admin, "PC-01")).GetProperty("taskManagerDisabled").GetBoolean());
        Assert.False((await ListEntryAsync(admin, "PC-02")).GetProperty("taskManagerDisabled").GetBoolean());
    }
}
