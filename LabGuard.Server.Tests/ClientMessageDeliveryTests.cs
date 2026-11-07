using System.Net;
using System.Text.Json;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;
using static LabGuard.Server.Tests.PcMessageEndpointTests;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

public class ClientMessageDeliveryTests
{
    // The messages a heartbeat hands over, or none when the field is left out.
    private static async Task<JsonElement[]> HandedMessagesAsync(HttpClient client, TestPc pc)
    {
        var body = await HeartbeatBodyAsync(client, pc);
        return body.TryGetProperty("messages", out var messages) ? messages.EnumerateArray().ToArray() : [];
    }

    private static string[] TextsOf(JsonElement[] messages) =>
        messages.Select(m => m.GetProperty("text").GetString()!).ToArray();

    private static async Task<PcMessage> StoredMessageAsync(TestApp app, long id) =>
        await app.WithDbAsync(db => db.PcMessages.AsNoTracking().SingleAsync(m => m.Id == id));

    // Heartbeats

    [Fact]
    public async Task Heartbeat_WithoutMessages_HasNoMessagesField()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);

        var body = await HeartbeatBodyAsync(client, pc);

        Assert.False(body.TryGetProperty("messages", out _));
    }

    [Fact]
    public async Task Heartbeat_HandsOverAMessage_WithItsIdTextAndSendTime()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedMessageAsync(app, pc, "Please save your work.");

        var messages = await HandedMessagesAsync(client, pc);

        var message = Assert.Single(messages);
        Assert.Equal(id, message.GetProperty("id").GetInt64());
        Assert.Equal("Please save your work.", message.GetProperty("text").GetString());
        // Stored and sent at millisecond precision, so the clock is compared at that precision.
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), message.GetProperty("sentUtc").GetDateTimeOffset().ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Heartbeat_HandsOverEachMessageOnce_AcrossTwoHeartbeats()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedMessageAsync(app, pc);

        var first = await HandedMessagesAsync(client, pc);
        var second = await HandedMessagesAsync(client, pc);

        Assert.Equal(id, first.Single().GetProperty("id").GetInt64());
        Assert.Empty(second);
        Assert.NotNull((await StoredMessageAsync(app, id)).HandedOverUtc);
    }

    [Fact]
    public async Task Heartbeat_HandsOverOldestFirst_AndAtMostFivePerHeartbeat()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();

        // Stored newest first, so the order of the ids is the reverse of the order they were sent in.
        for (var age = 1; age <= 6; age++)
        {
            await SeedMessageAsync(app, pc, $"m{age}", createdUtc: now - TimeSpan.FromMilliseconds(500 * age));
        }

        var firstHeartbeat = await HandedMessagesAsync(client, pc);
        var secondHeartbeat = await HandedMessagesAsync(client, pc);
        var thirdHeartbeat = await HandedMessagesAsync(client, pc);

        Assert.Equal(new[] { "m6", "m5", "m4", "m3", "m2" }, TextsOf(firstHeartbeat));
        Assert.Equal(new[] { "m1" }, TextsOf(secondHeartbeat));
        Assert.Empty(thirdHeartbeat);
    }

    [Fact]
    public async Task Heartbeat_DoesNotHandOverAMessageOutsideItsDeliveryWindow()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedMessageAsync(app, pc, createdUtc: app.Time.GetUtcNow() - TimeSpan.FromSeconds(7));

        var messages = await HandedMessagesAsync(client, pc);

        Assert.Empty(messages);
        Assert.Null((await StoredMessageAsync(app, id)).HandedOverUtc);
    }

    [Fact]
    public async Task Heartbeat_OfAnotherPc_DoesNotHandOverThisPcsMessage()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var other = await AddPcAsync(app, name: "PC-02");
        await SeedMessageAsync(app, other);

        Assert.Empty(await HandedMessagesAsync(client, pc));
    }

    // Acknowledgements

    [Fact]
    public async Task Ack_ByTheOwningPc_Returns204_AndMarksTheMessageDelivered()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedMessageAsync(app, pc);
        await HandedMessagesAsync(client, pc);

        var response = await AckAsync(client, pc, id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), (await StoredMessageAsync(app, id)).DeliveredUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Ack_Twice_Returns204BothTimes_AndKeepsTheFirstAcknowledgementTime()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedMessageAsync(app, pc);
        await HandedMessagesAsync(client, pc);

        Assert.Equal(HttpStatusCode.NoContent, (await AckAsync(client, pc, id)).StatusCode);
        var firstTime = (await StoredMessageAsync(app, id)).DeliveredUtc;
        app.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(HttpStatusCode.NoContent, (await AckAsync(client, pc, id)).StatusCode);
        Assert.Equal(firstTime, (await StoredMessageAsync(app, id)).DeliveredUtc);
    }

    [Fact]
    public async Task Ack_ByAnotherPc_Returns404_AndChangesNothing()
    {
        using var app = new TestApp();
        using var owner = app.CreatePcClient("192.168.1.50");
        using var intruder = app.CreatePcClient("192.168.1.51");
        var pc = await AddPcAsync(app);
        var other = await AddPcAsync(app, name: "PC-02");
        var id = await SeedMessageAsync(app, pc);
        await HandedMessagesAsync(owner, pc);

        var response = await AckAsync(intruder, other, id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Message not found.", (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null((await StoredMessageAsync(app, id)).DeliveredUtc);
    }

    [Fact]
    public async Task Ack_AfterSixtySeconds_Returns404()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedMessageAsync(app, pc);
        await HandedMessagesAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(61));

        var response = await AckAsync(client, pc, id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null((await StoredMessageAsync(app, id)).DeliveredUtc);
    }

    [Fact]
    public async Task Ack_LateButWithinSixtySeconds_Returns204_ButDoesNotMarkTheMessageDelivered()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedMessageAsync(app, pc);
        await HandedMessagesAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(30));

        var response = await AckAsync(client, pc, id);

        // The administrator was told the message failed, so the log must not say it was delivered.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null((await StoredMessageAsync(app, id)).DeliveredUtc);
    }

    [Fact]
    public async Task Ack_BeforeTheMessageWasHandedOver_Returns404()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedMessageAsync(app, pc);

        var response = await AckAsync(client, pc, id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null((await StoredMessageAsync(app, id)).DeliveredUtc);
    }

    [Fact]
    public async Task Ack_UnknownMessage_Returns404()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);

        var response = await AckAsync(client, pc, 999);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("wrong-token")]
    [InlineData(null)]
    public async Task Ack_WithoutAKnownToken_Returns401(string? token)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedMessageAsync(app, pc);
        await HandedMessagesAsync(client, pc);

        var response = await PostAsync(client, $"/api/client/messages/{id}/ack", token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null((await StoredMessageAsync(app, id)).DeliveredUtc);
    }
}
