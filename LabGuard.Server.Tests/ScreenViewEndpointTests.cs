using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using LabGuard.Server.Screen;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

// The administrator's live view: refusals before the upgrade, the view's lifetime, the close codes and the audit trail.
public class ScreenViewEndpointTests
{
    private const string LiveViewCloseReason = "The viewer closed.";

    private static async Task<WebSocket> OpenViewAsync(TestApp app, int pcId, string cookie, string query = "", string? origin = LiveViewClient.LocalOrigin)
    {
        var socket = await LiveViewClient.OpenAdminAsync(app, pcId, cookie, query, origin);
        // The first message comes after the viewer is registered, so the view is open once it arrives.
        Assert.Equal("{\"state\":\"waiting\"}", await LiveViewClient.ReceiveStateAsync(socket));
        return socket;
    }

    private static async Task<string> ViewIdAsync(TestApp app, string token)
    {
        var reply = await LiveViewClient.HeartbeatAsync(app, token);
        return reply.GetProperty("screenView").GetProperty("viewId").GetString()!;
    }

    private static async Task<HttpResponseMessage> AdminRequestAsync(TestApp app, HttpMethod method, string path, string cookie, object? body = null)
    {
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static async Task SetIdleTimeoutAsync(TestApp app, int minutes)
    {
        using var admin = await app.SignedInAdminAsync();
        var saved = await admin.PutAsJsonAsync("/api/settings", new
        {
            defaultApproval = new { mode = "time-limit", minutes = 60 },
            notice = new { isEnabled = true, text = "Notice" },
            idleTimeoutMinutes = minutes,
            defaultViewMode = "notified",
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    private static async Task<List<ScreenViewEvent>> AuditRowsAsync(TestApp app) =>
        await app.WithDbAsync(db => db.ScreenViewEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync());

    private static async Task AssertCloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        var message = await LiveViewClient.ReceiveAsync(socket);
        Assert.Equal(WebSocketMessageType.Close, message.Type);
        Assert.Equal(status, message.CloseStatus);
        Assert.Equal(reason, message.CloseReason);
    }

    // Refusals before the upgrade

    [Fact]
    public async Task Live_WithoutASession_IsRefused401()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);

        var status = await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenAdminAsync(app, id, cookie: null));

        Assert.Equal(401, status);
    }

    [Fact]
    public async Task Live_WithAnExpiredOrForgedCookie_IsRefused401()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);

        var status = await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenAdminAsync(app, id, "lykantra.admin=forged"));

        Assert.Equal(401, status);
    }

    [Fact]
    public async Task Live_WithoutAWebSocket_IsRefused400()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);

        using var response = await AdminRequestAsync(app, HttpMethod.Get, $"/api/pcs/{id}/screen/live", cookie);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost.evil.example:5173")]
    [InlineData("http://192.168.1.5:5000")]
    public async Task Live_FromAnOriginThatIsNotLoopback_IsRefused403(string? origin)
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);

        var status = await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenAdminAsync(app, id, cookie, origin: origin));

        Assert.Equal(403, status);
    }

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("http://[::1]:5173")]
    public async Task Live_FromALoopbackOrigin_OnAnyPort_IsAccepted(string origin)
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);

        using var socket = await OpenViewAsync(app, id, cookie, origin: origin);

        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Theory]
    [InlineData("?mode=secret")]
    [InlineData("?mode=")]
    [InlineData("?mode=Anonymous")]
    public async Task Live_WithAnInvalidMode_IsRefused400(string query)
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);

        var status = await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenAdminAsync(app, id, cookie, query));

        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Live_ForAnUnknownPc_IsRefused404()
    {
        using var app = new TestApp();
        var cookie = await LiveViewClient.SignInCookieAsync(app);

        var status = await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenAdminAsync(app, 999, cookie));

        Assert.Equal(404, status);
    }

    [Fact]
    public async Task Live_ForAnOfflinePc_IsRefused409()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        app.Time.Advance(TimeSpan.FromSeconds(16));
        var cookie = await LiveViewClient.SignInCookieAsync(app);

        var status = await LiveViewClient.RefusedStatusAsync(() => LiveViewClient.OpenAdminAsync(app, id, cookie));

        Assert.Equal(409, status);
    }

    [Fact]
    public async Task Live_OnThePcPort_IsNotServed()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        var clientPort = app.Services.GetRequiredService<ServerPorts>().ClientPort.ToString();

        var status = await LiveViewClient.RefusedStatusAsync(() =>
        {
            var socket = app.Server.CreateWebSocketClient();
            socket.ConfigureRequest = request =>
            {
                request.Headers["Cookie"] = cookie;
                request.Headers["Origin"] = LiveViewClient.LocalOrigin;
                request.Headers[SimulatedConnectionFilter.LocalPortHeader] = clientPort;
            };
            return socket.ConnectAsync(new Uri($"ws://localhost/api/pcs/{id}/screen/live"), CancellationToken.None);
        });

        Assert.Equal(404, status);
    }

    // Opening, modes and the audit trail

    [Fact]
    public async Task Live_OpensWaiting_AndRecordsTheStartWithoutAnEnd()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app, name: "Lab-Desk-3");
        var cookie = await LiveViewClient.SignInCookieAsync(app);

        using var socket = await OpenViewAsync(app, id, cookie);

        var row = Assert.Single(await AuditRowsAsync(app));
        Assert.Equal(TestApp.Username, row.AdminUsername);
        Assert.Equal("Lab-Desk-3", row.PcName);
        Assert.Equal(ScreenProtocol.NotifiedMode, row.Mode);
        Assert.Null(row.EndedAtUtc);
    }

    [Fact]
    public async Task Live_WithoutAMode_IsNotified()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);

        using var socket = await OpenViewAsync(app, id, cookie);

        Assert.Equal(ScreenProtocol.NotifiedMode, (await AuditRowsAsync(app)).Single().Mode);
    }

    [Fact]
    public async Task Live_WithAnonymousMode_IsRecordedAndReportedToThePc()
    {
        using var app = new TestApp();
        var (id, token) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);

        using var socket = await OpenViewAsync(app, id, cookie, "?mode=anonymous");

        Assert.Equal(ScreenProtocol.AnonymousMode, (await AuditRowsAsync(app)).Single().Mode);
        var reply = await LiveViewClient.HeartbeatAsync(app, token);
        Assert.Equal("anonymous", reply.GetProperty("screenView").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Live_ClosedByTheViewer_RecordsTheEnd_AndClosesThePcSocket()
    {
        using var app = new TestApp();
        var (id, token) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        using var browser = await OpenViewAsync(app, id, cookie);
        using var pc = await LiveViewClient.OpenPcAsync(app, token, await ViewIdAsync(app, token));
        Assert.Equal("{\"state\":\"live\"}", await LiveViewClient.ReceiveStateAsync(browser));

        await browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        var browserClose = await LiveViewClient.ReceiveAsync(browser);
        var pcClose = await LiveViewClient.ReceiveAsync(pc);

        Assert.Equal(WebSocketMessageType.Close, browserClose.Type);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, pcClose.CloseStatus);
        var row = Assert.Single(await AuditRowsAsync(app));
        Assert.NotNull(row.EndedAtUtc);
    }

    [Fact]
    public async Task Live_ANewViewer_ReplacesTheOldOneWith4001()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        using var first = await OpenViewAsync(app, id, cookie);

        using var second = await OpenViewAsync(app, id, cookie);

        await AssertCloseAsync(first, (WebSocketCloseStatus)4001, "Replaced by another viewer.");
        var rows = await AuditRowsAsync(app);
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].EndedAtUtc);
        Assert.Null(rows[1].EndedAtUtc);
    }

    [Fact]
    public async Task Live_PcGoesOffline_ClosesTheViewAndThePcSocketWith4002()
    {
        using var app = new TestApp();
        var (id, token) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        using var browser = await OpenViewAsync(app, id, cookie);
        using var pc = await LiveViewClient.OpenPcAsync(app, token, await ViewIdAsync(app, token));
        Assert.Equal("{\"state\":\"live\"}", await LiveViewClient.ReceiveStateAsync(browser));

        app.Time.Advance(TimeSpan.FromSeconds(16));
        await app.Services.GetRequiredService<ScreenViewMonitor>().CheckOnceAsync(CancellationToken.None);

        await AssertCloseAsync(browser, (WebSocketCloseStatus)4002, "The PC is offline or was removed.");
        await AssertCloseAsync(pc, (WebSocketCloseStatus)4002, "The PC is offline or was removed.");
    }

    [Fact]
    public async Task Live_PcRemoved_ClosesTheViewWith4002()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        using var browser = await OpenViewAsync(app, id, cookie);
        await app.WithDbAsync(async db => await db.Pcs.Where(p => p.Id == id).ExecuteDeleteAsync());

        await app.Services.GetRequiredService<ScreenViewMonitor>().CheckOnceAsync(CancellationToken.None);

        await AssertCloseAsync(browser, (WebSocketCloseStatus)4002, "The PC is offline or was removed.");
    }

    [Fact]
    public async Task Live_OnlineAndWithinItsSession_IsLeftOpenByTheMonitor()
    {
        using var app = new TestApp();
        var (id, token) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        using var browser = await OpenViewAsync(app, id, cookie);

        app.Time.Advance(TimeSpan.FromSeconds(5));
        await LiveViewClient.HeartbeatAsync(app, token);
        await app.Services.GetRequiredService<ScreenViewMonitor>().CheckOnceAsync(CancellationToken.None);

        Assert.NotNull(app.Services.GetRequiredService<ScreenStreamHub>().CurrentFor(id));
        Assert.Null((await AuditRowsAsync(app)).Single().EndedAtUtc);
    }

    // Session lifetime: logout, password change, expiry and no renewal

    [Fact]
    public async Task Live_AfterLogout_ClosesWith4003()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        using var browser = await OpenViewAsync(app, id, cookie);

        using var response = await AdminRequestAsync(app, HttpMethod.Post, "/api/auth/logout", cookie, new { });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertCloseAsync(browser, (WebSocketCloseStatus)4003, "The administrator session ended.");
    }

    [Fact]
    public async Task Live_AfterChangePassword_ClosesWith4003()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        using var browser = await OpenViewAsync(app, id, cookie);

        using var response = await AdminRequestAsync(app, HttpMethod.Post, "/api/auth/change-password", cookie,
            new { currentPassword = TestApp.Password, newPassword = "a brand new password" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertCloseAsync(browser, (WebSocketCloseStatus)4003, "The administrator session ended.");
    }

    [Fact]
    public async Task Live_WhenTheSessionIdleLimitPasses_ClosesWith4003_AndTheViewDidNotRenewIt()
    {
        using var app = new TestApp();
        var (id, token) = await LiveViewClient.AddPcAsync(app);
        await SetIdleTimeoutAsync(app, 5);

        var cookie = await LiveViewClient.SignInCookieAsync(app);
        app.Time.Advance(TimeSpan.FromMinutes(4));
        await LiveViewClient.HeartbeatAsync(app, token);
        using var browser = await OpenViewAsync(app, id, cookie);

        // The handshake did not renew the session, so the five minutes from sign-in run out two minutes later.
        app.Time.Advance(TimeSpan.FromMinutes(2));
        await LiveViewClient.HeartbeatAsync(app, token);
        await app.Services.GetRequiredService<ScreenViewMonitor>().CheckOnceAsync(CancellationToken.None);

        await AssertCloseAsync(browser, (WebSocketCloseStatus)4003, "The administrator session ended.");
    }

    [Fact]
    public async Task Live_AFrameAfterTheSessionEnds_ClosesWith4003_AndIsNotRelayed()
    {
        using var app = new TestApp();
        var (id, token) = await LiveViewClient.AddPcAsync(app);
        await SetIdleTimeoutAsync(app, 5);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        using var browser = await OpenViewAsync(app, id, cookie);
        using var pc = await LiveViewClient.OpenPcAsync(app, token, await ViewIdAsync(app, token));
        Assert.Equal("{\"state\":\"live\"}", await LiveViewClient.ReceiveStateAsync(browser));

        // The monitor checks every few seconds, so the relay itself must refuse a frame that arrives after the session ended.
        app.Time.Advance(TimeSpan.FromMinutes(6));
        await LiveViewClient.SendAsync(pc, LiveViewClient.Frame("late"));

        await AssertCloseAsync(browser, (WebSocketCloseStatus)4003, "The administrator session ended.");
    }

    // Shutdown and audit failure

    [Fact]
    public async Task Live_WhenTheServerStops_ClosesWith1001()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        using var browser = await OpenViewAsync(app, id, cookie);

        await app.Services.GetRequiredService<ScreenViewMonitor>().StopAsync(CancellationToken.None);

        await AssertCloseAsync(browser, WebSocketCloseStatus.EndpointUnavailable, "The server is stopping.");
    }

    [Fact]
    public async Task Live_WhenTheAuditCannotBeWritten_TheViewStillCloses_AndTheFailureIsLoggedWithoutDetails()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        using var browser = await OpenViewAsync(app, id, cookie);
        await app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE \"ScreenViewEvents\""));

        app.Time.Advance(TimeSpan.FromSeconds(16));
        await app.Services.GetRequiredService<ScreenViewMonitor>().CheckOnceAsync(CancellationToken.None);

        await AssertCloseAsync(browser, (WebSocketCloseStatus)4002, "The PC is offline or was removed.");
        var logs = string.Join("\n", app.Logs.Entries);
        Assert.Contains("Couldn't record the end of screen view", logs);
        Assert.DoesNotContain(cookie.Split('=')[1], logs);
    }

    // Browser messages

    [Fact]
    public async Task Live_BrowserMessageOverTheSmallLimit_IsClosedWith1009()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        using var browser = await OpenViewAsync(app, id, cookie);

        await LiveViewClient.SendTextAsync(browser, new string('x', 1024));

        await AssertCloseAsync(browser, WebSocketCloseStatus.MessageTooBig, "Message too large.");
    }
}
