using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Tests;

public class SettingsEndpointTests
{
    private const string DefaultNotice = "This computer may be monitored by the administrator.";

    private sealed record TestPc(int Id, string Token);

    private static async Task<HttpClient> SignedInAsync(TestApp app)
    {
        var client = app.CreateBrowser();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = TestApp.Username, password = TestApp.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    // Every field is sent, so a null here is a JSON null, which the server treats as missing.
    private static object Payload(
        string? mode = "time-limit",
        double? minutes = 60,
        bool? isEnabled = true,
        string? text = DefaultNotice,
        double? idleTimeoutMinutes = 30,
        string? defaultViewMode = "notified",
        double? powerCountdownSeconds = 30) =>
        new
        {
            defaultApproval = new { mode, minutes },
            notice = new { isEnabled, text },
            idleTimeoutMinutes,
            defaultViewMode,
            powerCountdownSeconds,
        };

    private static Task<HttpResponseMessage> GetSettingsAsync(HttpClient client) =>
        client.GetAsync("/api/settings");

    private static Task<HttpResponseMessage> PutSettingsAsync(HttpClient client, object payload) =>
        client.PutAsJsonAsync("/api/settings", payload);

    private static Task<int> RowCountAsync(TestApp app) =>
        app.WithDbAsync(db => db.ServerSettingsRows.CountAsync());

    private static async Task<TestPc> AddPcAsync(TestApp app, string name = "PC-01")
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var now = app.Time.GetUtcNow();
        var id = await app.WithDbAsync(async db =>
        {
            var pc = new Pc { Name = name, TokenHash = hash, RegisteredAtUtc = now, LastSeenUtc = now };
            db.Pcs.Add(pc);
            await db.SaveChangesAsync();
            return pc.Id;
        });
        return new TestPc(id, token);
    }

    private static Task<HttpResponseMessage> HeartbeatAsync(HttpClient client, TestPc pc)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/client/heartbeat");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {pc.Token}");
        return client.SendAsync(request);
    }

    // Defaults and creation

    [Fact]
    public async Task Get_FirstRead_ReturnsTheDefaults_AndCreatesOneRow()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await GetSettingsAsync(admin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal("time-limit", body.GetProperty("defaultApproval").GetProperty("mode").GetString());
        Assert.Equal(60, body.GetProperty("defaultApproval").GetProperty("minutes").GetInt32());
        Assert.True(body.GetProperty("notice").GetProperty("isEnabled").GetBoolean());
        Assert.Equal(DefaultNotice, body.GetProperty("notice").GetProperty("text").GetString());
        Assert.Equal(30, body.GetProperty("idleTimeoutMinutes").GetInt32());
        Assert.Equal("notified", body.GetProperty("defaultViewMode").GetString());
        Assert.Equal(30, body.GetProperty("powerCountdownSeconds").GetInt32());
        Assert.Equal(1, await RowCountAsync(app));
    }

    [Fact]
    public async Task Get_RepeatedReads_DoNotCreateMoreRows()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await GetSettingsAsync(admin)).StatusCode);
        }

        Assert.Equal(1, await RowCountAsync(app));
    }

    [Fact]
    public async Task Get_ConcurrentFirstReads_CreateExactlyOneRow()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => GetSettingsAsync(admin)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(1, await RowCountAsync(app));
    }

    // Saving

    [Fact]
    public async Task Put_Saves_AndReturnsTheSavedValue_WhichGetThenReturnsToo()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var payload = Payload(
            mode: "open-time",
            minutes: 120,
            isEnabled: false,
            text: "",
            idleTimeoutMinutes: 240,
            defaultViewMode: "anonymous");

        var put = await PutSettingsAsync(admin, payload);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await JsonOf(put);
        Assert.Equal("open-time", saved.GetProperty("defaultApproval").GetProperty("mode").GetString());
        Assert.Equal(120, saved.GetProperty("defaultApproval").GetProperty("minutes").GetInt32());
        Assert.False(saved.GetProperty("notice").GetProperty("isEnabled").GetBoolean());
        Assert.Equal("", saved.GetProperty("notice").GetProperty("text").GetString());
        Assert.Equal(240, saved.GetProperty("idleTimeoutMinutes").GetInt32());
        Assert.Equal("anonymous", saved.GetProperty("defaultViewMode").GetString());

        var read = await JsonOf(await GetSettingsAsync(admin));
        Assert.Equal(saved.ToString(), read.ToString());
    }

    [Fact]
    public async Task Put_TrimsTheNoticeText()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutSettingsAsync(admin, Payload(text: "  Lab rules apply.  "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Lab rules apply.", (await JsonOf(response)).GetProperty("notice").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("time-limit", 1, 5, 1)]
    [InlineData("time-limit", 1440, 480, 300)]
    [InlineData("open-time", 1, 5, 0)]
    public async Task Put_AcceptsTheBoundaryValues(string mode, double minutes, double idle, int noticeLength)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutSettingsAsync(admin, Payload(
            mode: mode,
            minutes: minutes,
            isEnabled: noticeLength > 0,
            text: new string('n', noticeLength),
            idleTimeoutMinutes: idle));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(noticeLength, body.GetProperty("notice").GetProperty("text").GetString()!.Length);
        Assert.Equal((int)idle, body.GetProperty("idleTimeoutMinutes").GetInt32());
    }

    [Theory]
    [InlineData("weekly", 60.0, true, "Notice", 30.0, "notified")]
    [InlineData("time-limit", 0.0, true, "Notice", 30.0, "notified")]
    [InlineData("time-limit", 1441.0, true, "Notice", 30.0, "notified")]
    [InlineData("time-limit", 1.5, true, "Notice", 30.0, "notified")]
    [InlineData("time-limit", null, true, "Notice", 30.0, "notified")]
    [InlineData("time-limit", 60.0, true, "Notice", 4.0, "notified")]
    [InlineData("time-limit", 60.0, true, "Notice", 481.0, "notified")]
    [InlineData("time-limit", 60.0, true, "Notice", 30.5, "notified")]
    [InlineData("time-limit", 60.0, true, "Notice", null, "notified")]
    [InlineData("time-limit", 60.0, true, "Notice", 30.0, "public")]
    [InlineData("time-limit", 60.0, true, "   ", 30.0, "notified")]
    [InlineData("time-limit", 60.0, true, null, 30.0, "notified")]
    [InlineData("time-limit", 60.0, null, "Notice", 30.0, "notified")]
    public async Task Put_WithAnInvalidValue_Returns400_AndChangesNothing(
        string mode,
        double? minutes,
        bool? isEnabled,
        string? text,
        double? idleTimeoutMinutes,
        string defaultViewMode)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutSettingsAsync(admin, Payload(mode, minutes, isEnabled, text, idleTimeoutMinutes, defaultViewMode));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await JsonOf(response)).GetProperty("error").GetString()));
        var read = await JsonOf(await GetSettingsAsync(admin));
        Assert.Equal(30, read.GetProperty("idleTimeoutMinutes").GetInt32());
        Assert.Equal(DefaultNotice, read.GetProperty("notice").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(3600.0)]
    public async Task Put_AcceptsTheCountdownBoundaries_AndGetReturnsThem(double seconds)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutSettingsAsync(admin, Payload(powerCountdownSeconds: seconds));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((int)seconds, (await JsonOf(response)).GetProperty("powerCountdownSeconds").GetInt32());
        Assert.Equal((int)seconds, (await JsonOf(await GetSettingsAsync(admin))).GetProperty("powerCountdownSeconds").GetInt32());
    }

    [Theory]
    [InlineData(4.0)]
    [InlineData(3601.0)]
    [InlineData(30.5)]
    [InlineData(null)]
    public async Task Put_WithAnInvalidPowerCountdown_Returns400WithTheLimits_AndChangesNothing(double? seconds)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutSettingsAsync(admin, Payload(powerCountdownSeconds: seconds));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "Power countdown must be a whole number of seconds from 5 to 3600.",
            (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(30, (await JsonOf(await GetSettingsAsync(admin))).GetProperty("powerCountdownSeconds").GetInt32());
    }

    [Fact]
    public async Task Put_NoticeLongerThan300Characters_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutSettingsAsync(admin, Payload(text: new string('n', 301)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_NoticeOf300CharactersAfterTrimming_IsAccepted()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutSettingsAsync(admin, Payload(text: new string(' ', 10) + new string('n', 300) + new string(' ', 10)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(300, (await JsonOf(response)).GetProperty("notice").GetProperty("text").GetString()!.Length);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"defaultApproval\":{\"mode\":\"time-limit\",\"minutes\":60}}")]
    [InlineData("{\"defaultApproval\":null,\"notice\":{\"isEnabled\":true,\"text\":\"x\"},\"idleTimeoutMinutes\":30,\"defaultViewMode\":\"notified\"}")]
    [InlineData("{\"defaultApproval\":{\"mode\":\"time-limit\",\"minutes\":60},\"notice\":{\"isEnabled\":true,\"text\":\"x\"},\"idleTimeoutMinutes\":\"thirty\",\"defaultViewMode\":\"notified\"}")]
    [InlineData("{\"defaultApproval\":{\"mode\":\"time-limit\",\"minutes\":60},\"notice\":{\"isEnabled\":true,\"text\":\"x\"},\"idleTimeoutMinutes\":30,\"defaultViewMode\":\"notified\"}")]
    public async Task Put_WithAMissingOrUnreadableSection_Returns400WithAnError(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await admin.PutAsync("/api/settings", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await JsonOf(response)).GetProperty("error").GetString()));
        Assert.Equal(30, (await JsonOf(await GetSettingsAsync(admin))).GetProperty("idleTimeoutMinutes").GetInt32());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{not json")]
    public async Task Put_WithANullOrMalformedBody_Returns400InvalidRequest(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await admin.PutAsync("/api/settings", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid request.", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_WithoutAJsonContentType_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var content = new StringContent("{}", Encoding.UTF8, "text/plain");

        var response = await admin.PutAsync("/api/settings", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Access

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task Settings_WithoutSignIn_Return401(string method)
    {
        using var app = new TestApp();
        using var anonymous = app.CreateBrowser();

        var response = method == "GET"
            ? await GetSettingsAsync(anonymous)
            : await PutSettingsAsync(anonymous, Payload());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await RowCountAsync(app));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task Settings_FromThePcPort_Return404(string method)
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient();

        var response = method == "GET"
            ? await GetSettingsAsync(pcClient)
            : await PutSettingsAsync(pcClient, Payload());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await RowCountAsync(app));
    }

    // Heartbeat notice

    [Fact]
    public async Task Heartbeat_ReportsTheDefaultNotice()
    {
        using var app = new TestApp();
        var pc = await AddPcAsync(app);
        using var client = app.CreatePcClient();

        var body = await JsonOf(await HeartbeatAsync(client, pc));

        Assert.Equal(DefaultNotice, body.GetProperty("notice").GetString());
    }

    [Fact]
    public async Task Heartbeat_WithTheNoticeOff_ReportsNull()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await PutSettingsAsync(admin, Payload(isEnabled: false, text: "Hidden text"));
        var pc = await AddPcAsync(app);
        using var client = app.CreatePcClient();

        var body = await JsonOf(await HeartbeatAsync(client, pc));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("notice").ValueKind);
    }

    [Fact]
    public async Task Heartbeat_AfterAnEdit_ReportsTheNewText()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        using var client = app.CreatePcClient();
        await PutSettingsAsync(admin, Payload(text: "Lab rules apply."));

        var body = await JsonOf(await HeartbeatAsync(client, pc));

        Assert.Equal("Lab rules apply.", body.GetProperty("notice").GetString());
    }
}
