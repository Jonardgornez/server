using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

public class PcMessageEndpointTests
{
    private const string SendPath = "/api/pcs/messages";
    private const string Hello = "Please save your work.";

    internal static Task<HttpResponseMessage> SendMessageAsync(HttpClient admin, object body) =>
        admin.PostAsJsonAsync(SendPath, body);

    internal static Task<PcMessage[]> StoredMessagesAsync(TestApp app) =>
        app.WithDbAsync(db => db.PcMessages.AsNoTracking().OrderBy(m => m.Id).ToArrayAsync());

    // Stores a message for the PC as if an administrator had sent it. Returns its id.
    internal static Task<long> SeedMessageAsync(TestApp app, TestPc pc, string text = Hello, DateTimeOffset? createdUtc = null) =>
        app.WithDbAsync(async db =>
        {
            var message = new PcMessage
            {
                PcId = pc.Id,
                PcName = await db.Pcs.Where(p => p.Id == pc.Id).Select(p => p.Name).SingleAsync(),
                AdminUserName = TestApp.Username,
                Text = text,
                CreatedUtc = createdUtc ?? app.Time.GetUtcNow(),
            };
            db.PcMessages.Add(message);
            await db.SaveChangesAsync();
            return message.Id;
        });

    // Waits until a send has stored the given number of messages for the PC, so the test can answer it while the send is still waiting.
    internal static async Task WaitForStoredMessagesAsync(TestApp app, int pcId, int count)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (await app.WithDbAsync(db => db.PcMessages.CountAsync(m => m.PcId == pcId)) >= count)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The send did not store its message.");
    }

    internal static Task<HttpResponseMessage> AckAsync(HttpClient pc, TestPc owner, long messageId) =>
        PostAsync(pc, $"/api/client/messages/{messageId}/ack", owner.Token);

    // The send's refusal, checked the same way for every bad input.
    private static async Task AssertRefusedAsync(TestApp app, HttpResponseMessage response, string error)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Empty(await StoredMessagesAsync(app));
    }

    // Admin route access

    [Fact]
    public async Task Send_WithoutCookie_Returns401AndStoresNothing()
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();
        var pc = await AddPcAsync(app);

        var response = await browser.PostAsJsonAsync(SendPath, new { pcIds = new[] { pc.Id }, text = Hello });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await StoredMessagesAsync(app));
    }

    [Fact]
    public async Task Send_FromThePcPort_Returns404()
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);

        var response = await pcClient.PostAsJsonAsync(SendPath, new { pcIds = new[] { pc.Id }, text = Hello });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await StoredMessagesAsync(app));
    }

    [Theory]
    [InlineData("text/plain", "{}")]
    [InlineData("application/x-www-form-urlencoded", "")]
    [InlineData("application/json", "")]
    [InlineData("application/json", "null")]
    [InlineData("application/json", "{not json")]
    public async Task Send_WithoutAJsonBody_Returns400AndStoresNothing(string contentType, string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await AddPcAsync(app);

        using var content = new StringContent(body, Encoding.UTF8, contentType);
        var response = await admin.PostAsync(SendPath, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Empty(await StoredMessagesAsync(app));
    }

    // Text validation

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t ")]
    public async Task Send_BlankText_Returns400AndStoresNothing(string text)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var response = await SendMessageAsync(admin, new { pcIds = new[] { pc.Id }, text });

        await AssertRefusedAsync(app, response, "Enter a message.");
    }

    [Theory]
    [InlineData("tab\tinside")]
    [InlineData("bell\u0007")]
    public async Task Send_ControlCharacter_Returns400AndStoresNothing(string text)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var response = await SendMessageAsync(admin, new { pcIds = new[] { pc.Id }, text });

        await AssertRefusedAsync(app, response, "A message can't contain control characters.");
    }

    [Fact]
    public async Task Send_MissingText_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var response = await SendMessageAsync(admin, new { pcIds = new[] { pc.Id }, text = (string?)null });

        await AssertRefusedAsync(app, response, "Enter a message.");
    }

    [Fact]
    public async Task Send_501Characters_Returns400AndStoresNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var response = await SendMessageAsync(admin, new { pcIds = new[] { pc.Id }, text = new string('a', 501) });

        await AssertRefusedAsync(app, response, "A message can be at most 500 characters.");
    }

    [Fact]
    public async Task Send_ElevenLines_Returns400AndStoresNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var response = await SendMessageAsync(admin, new { pcIds = new[] { pc.Id }, text = string.Join("\r\n", Enumerable.Repeat("line", 11)) });

        await AssertRefusedAsync(app, response, "A message can be at most 10 lines.");
    }

    [Fact]
    public async Task Send_LoneSurrogateEscape_Returns400AndStoresNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        // The JSON reader refuses a lone surrogate escape before the text is checked, so it is the same bad request as a malformed body.
        using var content = new StringContent($"{{\"pcIds\":[{pc.Id}],\"text\":\"a\\ud800b\"}}", Encoding.UTF8, "application/json");
        var response = await admin.PostAsync(SendPath, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Empty(await StoredMessagesAsync(app));
    }

    // Recipient validation

    [Fact]
    public async Task Send_NoPcIds_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await SendMessageAsync(admin, new { pcIds = Array.Empty<int>(), text = Hello });

        await AssertRefusedAsync(app, response, "Choose at least one PC.");
    }

    [Fact]
    public async Task Send_MissingPcIds_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await SendMessageAsync(admin, new { text = Hello });

        await AssertRefusedAsync(app, response, "Choose at least one PC.");
    }

    [Fact]
    public async Task Send_MoreThan100PcIds_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await SendMessageAsync(admin, new { pcIds = Enumerable.Range(1, 101).ToArray(), text = Hello });

        await AssertRefusedAsync(app, response, "Choose at most 100 PCs.");
    }

    [Fact]
    public async Task Send_DuplicatePcId_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var response = await SendMessageAsync(admin, new { pcIds = new[] { pc.Id, pc.Id }, text = Hello });

        await AssertRefusedAsync(app, response, "Each PC can be chosen only once.");
    }

    // Results

    [Fact]
    public async Task Send_UnknownPc_ReportsNotFound_WithNoName()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await SendMessageAsync(admin, new { pcIds = new[] { 999 }, text = Hello });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await JsonOf(response)).GetProperty("results")[0];
        Assert.Equal(999, result.GetProperty("pcId").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("pcName").ValueKind);
        Assert.Equal("not-found", result.GetProperty("status").GetString());
        Assert.Empty(await StoredMessagesAsync(app));
    }

    [Fact]
    public async Task Send_OfflinePc_ReportsOffline_AndStoresNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, seenAgo: TimeSpan.FromMinutes(1));

        var response = await SendMessageAsync(admin, new { pcIds = new[] { pc.Id }, text = Hello });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await JsonOf(response)).GetProperty("results")[0];
        Assert.Equal("PC-01", result.GetProperty("pcName").GetString());
        Assert.Equal("offline", result.GetProperty("status").GetString());
        Assert.Empty(await StoredMessagesAsync(app));
    }

    [Fact]
    public async Task Send_ResultsFollowTheOrderThePcsWereListedIn()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var offline = await AddPcAsync(app, name: "PC-02", seenAgo: TimeSpan.FromMinutes(1));

        var response = await SendMessageAsync(admin, new { pcIds = new[] { 999, offline.Id }, text = Hello });

        var results = (await JsonOf(response)).GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(new[] { 999, offline.Id }, results.Select(r => r.GetProperty("pcId").GetInt32()).ToArray());
        Assert.Equal(new[] { "not-found", "offline" }, results.Select(r => r.GetProperty("status").GetString()).ToArray());
    }

    [Fact]
    public async Task Send_FivePendingMessagesAlready_ReportsBusy_AndStoresNothingNew()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        for (var index = 0; index < 5; index++)
        {
            await SeedMessageAsync(app, pc);
        }

        var response = await SendMessageAsync(admin, new { pcIds = new[] { pc.Id }, text = Hello });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("busy", (await JsonOf(response)).GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal(5, (await StoredMessagesAsync(app)).Length);
    }

    [Fact]
    public async Task Send_MessagesPastTheirDeliveryWindow_DoNotCountTowardBusy()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var old = app.Time.GetUtcNow() - TimeSpan.FromSeconds(7);
        for (var index = 0; index < 5; index++)
        {
            await SeedMessageAsync(app, pc, createdUtc: old);
        }

        var sending = SendMessageAsync(admin, new { pcIds = new[] { pc.Id }, text = Hello });
        await WaitForStoredMessagesAsync(app, pc.Id, 6);
        app.Time.Advance(TimeSpan.FromSeconds(6));

        var response = await sending;
        Assert.Equal("failed", (await JsonOf(response)).GetProperty("results")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Send_MessagesDatedInTheFuture_DoNotCountTowardBusy()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var future = app.Time.GetUtcNow() + TimeSpan.FromSeconds(1);
        for (var index = 0; index < 5; index++)
        {
            await SeedMessageAsync(app, pc, createdUtc: future);
        }

        var sending = SendMessageAsync(admin, new { pcIds = new[] { pc.Id }, text = Hello });
        await WaitForStoredMessagesAsync(app, pc.Id, 6);
        app.Time.Advance(TimeSpan.FromSeconds(6));

        var response = await sending;
        Assert.Equal("failed", (await JsonOf(response)).GetProperty("results")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Send_DirectionOverrideCharacter_Returns400AndStoresNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var response = await SendMessageAsync(admin, new { pcIds = new[] { pc.Id }, text = "a\u202Eb" });

        await AssertRefusedAsync(app, response, "The message can't contain direction or invisible formatting characters.");
    }

    [Fact]
    public async Task Send_WhileTwoSendsAreInFlight_Returns429AndStoresNothingNew_AndTheSlotIsFreedAfterwards()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var first = await AddPcAsync(app, name: "PC-01");
        var second = await AddPcAsync(app, name: "PC-02");
        var third = await AddPcAsync(app, name: "PC-03");
        var offline = await AddPcAsync(app, name: "PC-04", seenAgo: TimeSpan.FromMinutes(1));

        var sendingFirst = SendMessageAsync(admin, new { pcIds = new[] { first.Id }, text = Hello });
        await WaitForStoredMessagesAsync(app, first.Id, 1);
        var sendingSecond = SendMessageAsync(admin, new { pcIds = new[] { second.Id }, text = Hello });
        await WaitForStoredMessagesAsync(app, second.Id, 1);

        var refused = await SendMessageAsync(admin, new { pcIds = new[] { third.Id }, text = Hello });

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("A message is already being sent. Try again in a moment.", (await JsonOf(refused)).GetProperty("error").GetString());
        Assert.Equal(2, (await StoredMessagesAsync(app)).Length);

        app.Time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(HttpStatusCode.OK, (await sendingFirst).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sendingSecond).StatusCode);

        // Both slots are free again. An offline PC answers at once, so this only checks that a send is accepted.
        var after = await SendMessageAsync(admin, new { pcIds = new[] { offline.Id }, text = Hello });
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Send_PcAcknowledgesInTime_ReportsDelivered_AndLogsEveryField()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);

        var sending = SendMessageAsync(admin, new { pcIds = new[] { pc.Id }, text = Hello });
        await WaitForStoredMessagesAsync(app, pc.Id, 1);
        var heartbeat = await HeartbeatBodyAsync(client, pc);
        var messageId = heartbeat.GetProperty("messages")[0].GetProperty("id").GetInt64();
        var acknowledged = await AckAsync(client, pc, messageId);
        Assert.Equal(HttpStatusCode.NoContent, acknowledged.StatusCode);

        var response = await sending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await JsonOf(response)).GetProperty("results")[0];
        Assert.Equal(pc.Id, result.GetProperty("pcId").GetInt32());
        Assert.Equal("PC-01", result.GetProperty("pcName").GetString());
        Assert.Equal("delivered", result.GetProperty("status").GetString());

        var stored = (await StoredMessagesAsync(app)).Single();
        Assert.Equal(messageId, stored.Id);
        Assert.Equal(pc.Id, stored.PcId);
        Assert.Equal("PC-01", stored.PcName);
        Assert.Equal(TestApp.Username, stored.AdminUserName);
        Assert.Equal(Hello, stored.Text);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.CreatedUtc.ToUnixTimeMilliseconds());
        Assert.NotNull(stored.HandedOverUtc);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.DeliveredUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Send_NoAckWithinSixSeconds_ReportsFailed_AndKeepsTheRow()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var sending = SendMessageAsync(admin, new { pcIds = new[] { pc.Id }, text = "  first\r\nsecond  " });
        await WaitForStoredMessagesAsync(app, pc.Id, 1);
        app.Time.Advance(TimeSpan.FromSeconds(6));

        var response = await sending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("failed", (await JsonOf(response)).GetProperty("results")[0].GetProperty("status").GetString());

        var stored = (await StoredMessagesAsync(app)).Single();
        Assert.Equal("first\nsecond", stored.Text);
        Assert.Null(stored.HandedOverUtc);
        Assert.Null(stored.DeliveredUtc);
    }

    [Fact]
    public async Task Send_MixedPcs_EachGetsItsOwnOutcome()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var answering = await AddPcAsync(app, name: "PC-01");
        var silent = await AddPcAsync(app, name: "PC-02");
        var offline = await AddPcAsync(app, name: "PC-03", seenAgo: TimeSpan.FromMinutes(1));

        var sending = SendMessageAsync(admin, new { pcIds = new[] { answering.Id, silent.Id, offline.Id }, text = Hello });
        await WaitForStoredMessagesAsync(app, answering.Id, 1);
        await WaitForStoredMessagesAsync(app, silent.Id, 1);
        var messageId = (await HeartbeatBodyAsync(client, answering)).GetProperty("messages")[0].GetProperty("id").GetInt64();
        await AckAsync(client, answering, messageId);
        app.Time.Advance(TimeSpan.FromSeconds(6));

        var results = (await JsonOf(await sending)).GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(new[] { "delivered", "failed", "offline" }, results.Select(r => r.GetProperty("status").GetString()).ToArray());
    }
}
