using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static LabGuard.Server.Tests.ClientUpdateTestSupport;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The administrator's view of a PC's latest service report.
public class PcDiagnosticsEndpointTests
{
    private const string ServiceReportRoute = "/api/client/service-report";
    private const string NoReportMessage = "No report from this PC yet.";
    private const string NotFoundMessage = "PC not found.";

    private static string DiagnosticsRoute(int pcId) => $"/api/pcs/{pcId}/diagnostics";

    [Fact]
    public async Task Diagnostics_ReturnsTheLatestReport_WithItsShape_AndNoStore()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01", seenAgo: TimeSpan.FromMinutes(5));
        await PostAsync(pc, ServiceReportRoute, registered.Token, JsonContent.Create(new
        {
            serviceVersion = "0.23.0",
            clientRunning = true,
            clientVersion = "0.22.3",
            userSignedIn = false,
            update = new { version = "0.23.0", state = "installing", error = (string?)null },
            events = new[] { new { at = "2026-11-11T05:00:00Z", level = "error", message = "Client stopped." } },
        }));

        var response = await admin.GetAsync(DiagnosticsRoute(registered.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var body = await JsonOf(response);
        Assert.Equal(
            new[] { "clientRunning", "clientVersion", "events", "receivedAt", "serviceVersion", "update", "userSignedIn" },
            body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), body.GetProperty("receivedAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
        Assert.Equal("0.23.0", body.GetProperty("serviceVersion").GetString());
        Assert.True(body.GetProperty("clientRunning").GetBoolean());
        Assert.Equal("0.22.3", body.GetProperty("clientVersion").GetString());
        Assert.False(body.GetProperty("userSignedIn").GetBoolean());
        var update = body.GetProperty("update");
        Assert.Equal("0.23.0", update.GetProperty("version").GetString());
        Assert.Equal("installing", update.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, update.GetProperty("error").ValueKind);
        var events = body.GetProperty("events");
        Assert.Equal(1, events.GetArrayLength());
        Assert.Equal("error", events[0].GetProperty("level").GetString());
        Assert.Equal("Client stopped.", events[0].GetProperty("message").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-11-11T05:00:00Z").ToUnixTimeMilliseconds(), events[0].GetProperty("at").GetDateTimeOffset().ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Diagnostics_WithAReportThatHasNoUpdate_ShowsNullUpdate()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");
        await PostAsync(pc, ServiceReportRoute, registered.Token, JsonContent.Create(new { clientRunning = true }));

        var body = await JsonOf(await admin.GetAsync(DiagnosticsRoute(registered.Id)));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("update").ValueKind);
        Assert.Equal(0, body.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task Diagnostics_ForAPcWithNoReport_Returns404()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var id = (await AddPcAsync(app, "PC-01")).Id;

        var response = await admin.GetAsync(DiagnosticsRoute(id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(NoReportMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Diagnostics_ForAnUnknownPc_Returns404()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await admin.GetAsync(DiagnosticsRoute(999));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(NotFoundMessage, (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Diagnostics_WithoutACookie_Returns401()
    {
        using var app = new TestApp();
        using var anonymous = app.CreateBrowser();
        var id = (await AddPcAsync(app, "PC-01")).Id;

        var response = await anonymous.GetAsync(DiagnosticsRoute(id));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Diagnostics_OnThePcPort_IsNotFound()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var id = (await AddPcAsync(app, "PC-01")).Id;

        var response = await pc.GetAsync(DiagnosticsRoute(id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
