using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LabGuard.Server.ClientUpdates;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static LabGuard.Server.Tests.ClientUpdateTestSupport;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The service report route: what is saved for a PC, the update offer it gets back, the limits it is read with, and how it is refused.
public class ServiceReportEndpointTests
{
    private const string ServiceReportRoute = "/api/client/service-report";
    private const string NotRegisteredMessage = "This PC isn't registered.";
    private const string InvalidMessage = "Invalid request.";

    private static async Task<(TestApp App, HttpClient Admin, HttpClient Pc, FakeInstallerInspector Inspector)> AppWithReleaseAsync(string version = "0.20.0")
    {
        var inspector = new FakeInstallerInspector();
        var app = new TestApp(configureServices: services =>
        {
            services.RemoveAll<IInstallerInspector>();
            services.AddSingleton<IInstallerInspector>(inspector);
        });
        var admin = await SignedInAsync(app);
        await StoreReleaseAsync(admin, inspector, version);
        return (app, admin, app.CreatePcClient(OtherPcAddress), inspector);
    }

    // Offers the stored release to the PC, so its target is set.
    private static async Task<TestPc> TargetedPcAsync(TestApp app, HttpClient admin, string name = "PC-01")
    {
        var pc = await AddPcAsync(app, name);
        Assert.Equal(1, await AffectedAsync(await OfferStoredAsync(admin, pc.Id)));
        return pc;
    }

    private static Task<HttpResponseMessage> ReportAsync(HttpClient pc, TestPc registered, object body) =>
        PostAsync(pc, ServiceReportRoute, registered.Token, JsonContent.Create(body));

    private static Task<HttpResponseMessage> RawReportAsync(HttpClient pc, string token, HttpContent content) =>
        PostAsync(pc, ServiceReportRoute, token, content);

    private static Task<PcServiceReport?> StoredReportAsync(TestApp app, int pcId) =>
        app.WithDbAsync(db => db.PcServiceReports.AsNoTracking().FirstOrDefaultAsync(r => r.PcId == pcId));

    private static async Task<ServiceReportEvent[]> StoredEventsAsync(TestApp app, int pcId) =>
        JsonSerializer.Deserialize<ServiceReportEvent[]>((await StoredReportAsync(app, pcId))!.EventsJson)!;

    private static async Task<int> ReportCountAsync(TestApp app) =>
        await app.WithDbAsync(db => db.PcServiceReports.CountAsync());

    private static object FullReport(string clientVersion = "0.23.0") => new
    {
        serviceVersion = "0.23.0",
        clientRunning = true,
        clientVersion,
        userSignedIn = true,
        update = new { version = "0.23.0", state = "downloading", error = (string?)null },
        events = new[] { new { at = "2026-11-11T05:00:00Z", level = "warning", message = "Client restarted." } },
    };

    // Saved

    [Fact]
    public async Task Report_IsSaved_WithItsValuesAndTheReceiveTime()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");

        var response = await ReportAsync(pc, registered, FullReport());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        var stored = await StoredReportAsync(app, registered.Id);
        Assert.Equal("0.23.0", stored!.ServiceVersion);
        Assert.True(stored.ClientRunning);
        Assert.Equal("0.23.0", stored.ClientVersion);
        Assert.True(stored.UserSignedIn);
        Assert.Equal("0.23.0", stored.UpdateVersion);
        Assert.Equal("downloading", stored.UpdateState);
        Assert.Null(stored.UpdateError);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.ReceivedAtUtc.ToUnixTimeMilliseconds());
        var events = await StoredEventsAsync(app, registered.Id);
        Assert.Equal([new ServiceReportEvent(DateTimeOffset.Parse("2026-11-11T05:00:00Z"), "warning", "Client restarted.")], events);
    }

    [Fact]
    public async Task Report_LaterOne_ReplacesTheEarlierOne_AndKeepsOneRow()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");
        await ReportAsync(pc, registered, new { clientRunning = true, serviceVersion = "0.23.0" });
        app.Time.Advance(TimeSpan.FromMinutes(1));

        var response = await ReportAsync(pc, registered, new { clientRunning = false, serviceVersion = "0.24.0", events = new[] { new { at = "2026-11-11T05:01:00Z", level = "error", message = "Stopped." } } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await ReportCountAsync(app));
        var stored = await StoredReportAsync(app, registered.Id);
        Assert.False(stored!.ClientRunning);
        Assert.Equal("0.24.0", stored.ServiceVersion);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.ReceivedAtUtc.ToUnixTimeMilliseconds());
        Assert.Equal("Stopped.", (await StoredEventsAsync(app, registered.Id)).Single().Message);
    }

    [Fact]
    public async Task Report_DoesNotChangeLastSeen()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01", seenAgo: TimeSpan.FromMinutes(5));
        var lastSeen = (await StoredPcAsync(app, registered.Id)).LastSeenUtc;

        await ReportAsync(pc, registered, FullReport());

        Assert.Equal(lastSeen.ToUnixTimeMilliseconds(), (await StoredPcAsync(app, registered.Id)).LastSeenUtc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Report_IsRemovedWithThePc()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01", seenAgo: TimeSpan.FromMinutes(5));
        await ReportAsync(pc, registered, FullReport());
        using var admin = await SignedInAsync(app);

        var response = await admin.DeleteAsync($"/api/pcs/{registered.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await ReportCountAsync(app));
    }

    // Lenient reading

    [Theory]
    [InlineData("0.23")]
    [InlineData("v0.23.0")]
    [InlineData("0.23.0.1")]
    [InlineData("0.23.0\n")]
    public async Task Report_StoresAnUnreadableServiceOrClientVersion_AsNull(string version)
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");

        var response = await ReportAsync(pc, registered, new { serviceVersion = version, clientVersion = version, clientRunning = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredReportAsync(app, registered.Id);
        Assert.Null(stored!.ServiceVersion);
        Assert.Null(stored.ClientVersion);
    }

    [Fact]
    public async Task Report_WithAnUnreadableClientVersion_LeavesTheStoredVersionAlone()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");
        await HeartbeatWithAsync(pc, registered, new { clientVersion = "0.22.3" });

        await ReportAsync(pc, registered, new { clientVersion = "0.22" });

        Assert.Equal("0.22.3", (await StoredPcAsync(app, registered.Id)).ClientVersion);
    }

    [Fact]
    public async Task Report_WithoutAClientVersion_LeavesTheStoredVersionAlone()
    {
        // Unlike the heartbeat, a report without a version doesn't make the stored version unknown: the heartbeat is the source that sets it.
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");
        await HeartbeatWithAsync(pc, registered, new { clientVersion = "0.22.3" });

        await ReportAsync(pc, registered, new { clientRunning = true });

        Assert.Equal("0.22.3", (await StoredPcAsync(app, registered.Id)).ClientVersion);
    }

    [Fact]
    public async Task Report_WithAReadableClientVersion_StoresItAsTheClientVersion()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");

        await ReportAsync(pc, registered, new { clientVersion = "0.21.0" });

        Assert.Equal("0.21.0", (await StoredPcAsync(app, registered.Id)).ClientVersion);
    }

    [Fact]
    public async Task Report_WithWronglyTypedParts_StillSaves_WithThoseParts_AsFalseOrNullOrEmpty()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");

        var response = await RawReportAsync(pc, registered.Token, new StringContent(
            """{"serviceVersion":5,"clientRunning":"yes","clientVersion":true,"userSignedIn":1,"update":"oops","events":"none"}""",
            Encoding.UTF8,
            "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredReportAsync(app, registered.Id);
        Assert.Null(stored!.ServiceVersion);
        Assert.False(stored.ClientRunning);
        Assert.Null(stored.ClientVersion);
        Assert.False(stored.UserSignedIn);
        Assert.Null(stored.UpdateState);
        Assert.Empty(await StoredEventsAsync(app, registered.Id));
    }

    [Theory]
    [InlineData("pending", "pending")]
    [InlineData("waiting", "waiting")]
    [InlineData("downloading", "downloading")]
    [InlineData("installing", "installing")]
    [InlineData("failed", "failed")]
    [InlineData("rebooting", null)]
    [InlineData("FAILED", null)]
    [InlineData("", null)]
    public async Task Report_KeepsOnlyTheUpdateStatesTheServiceReports(string state, string? expected)
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");

        await ReportAsync(pc, registered, new { update = new { version = "0.23.0", state, error = "x" } });

        Assert.Equal(expected, (await StoredReportAsync(app, registered.Id))!.UpdateState);
    }

    [Fact]
    public async Task Report_CleansTheUpdateError_AndCapsItAt200Characters()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");

        await ReportAsync(pc, registered, new { update = new { version = "0.23.0", state = "failed", error = "  Disk‮ full\u0000\n" + new string('x', 300) } });

        var error = (await StoredReportAsync(app, registered.Id))!.UpdateError;
        Assert.Equal(200, error!.Length);
        Assert.StartsWith("Disk full", error);
        Assert.DoesNotContain('‮', error);
    }

    [Fact]
    public async Task Report_WithAnUpdateWithNothingReadable_StoresNoUpdate()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");

        await ReportAsync(pc, registered, new { update = new { version = "x", state = "rebooting", error = "\n" } });

        var stored = await StoredReportAsync(app, registered.Id);
        Assert.Null(stored!.UpdateVersion);
        Assert.Null(stored.UpdateState);
        Assert.Null(stored.UpdateError);
    }

    [Fact]
    public async Task Report_KeepsAtMost20ReadableEvents_InTheOrderSent()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");
        var events = Enumerable.Range(0, 25).Select(i => new { at = "2026-11-11T05:00:00Z", level = "information", message = $"m{i}" });

        await ReportAsync(pc, registered, new { events });

        var stored = await StoredEventsAsync(app, registered.Id);
        Assert.Equal(20, stored.Length);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"m{i}"), stored.Select(e => e.Message));
    }

    [Fact]
    public async Task Report_LeavesOutEventsItCannotRead()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");

        await ReportAsync(pc, registered, new
        {
            events = new object[]
            {
                new { at = "2026-11-11T05:00:00Z", level = "debug", message = "unknown level" },
                new { at = "2026-11-11T05:00:00Z", level = "message", message = "not a level the admin understands" },
                new { at = "not a time", level = "warning", message = "bad time" },
                new { at = "2026-11-11T05:00:00Z", level = "warning", message = "‎ \n" },
                new { at = "2026-11-11T05:00:00Z", level = 3, message = "wrong type" },
                5,
                new { at = "2026-11-11T05:00:00Z", level = "information", message = "kept" },
            },
        });

        Assert.Equal(["kept"], (await StoredEventsAsync(app, registered.Id)).Select(e => e.Message));
    }

    [Fact]
    public async Task Report_CleansAndCapsEventMessages_At300Characters()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");
        var message = "a\u0000b" + new string('x', 400);

        await ReportAsync(pc, registered, new { events = new[] { new { at = "2026-11-11T05:00:00Z", level = "error", message } } });

        var stored = (await StoredEventsAsync(app, registered.Id)).Single();
        Assert.Equal(300, stored.Message.Length);
        Assert.StartsWith("ab", stored.Message);
    }

    [Fact]
    public async Task Report_ConvertsEventTimesToUtc()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");

        await ReportAsync(pc, registered, new { events = new[] { new { at = "2026-11-11T13:00:00+08:00", level = "information", message = "local" } } });

        var stored = (await StoredEventsAsync(app, registered.Id)).Single();
        Assert.Equal(DateTimeOffset.Parse("2026-11-11T05:00:00Z"), stored.At);
        Assert.Equal(TimeSpan.Zero, stored.At.Offset);
    }

    // Offers

    [Fact]
    public async Task Report_OffersTheStoredRelease_WhenTheTargetMatchesAndThePcIsBehind()
    {
        var (app, admin, pc, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        var response = await ReportAsync(pc, registered, new { clientVersion = "0.19.0" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var offer = await ClientUpdateOfHeartbeatAsync(response);
        Assert.NotNull(offer);
        Assert.Equal("0.20.0", offer!.Value.GetProperty("version").GetString());
        Assert.Equal(InstallerBytesLength("0.20.0"), offer.Value.GetProperty("sizeBytes").GetInt64());
        Assert.False(offer.Value.GetProperty("installNow").GetBoolean());
    }

    [Fact]
    public async Task Report_OffersTheSameInstallNowChoice_AsTheHeartbeat()
    {
        var (app, admin, pc, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await AddPcAsync(app, "PC-01");
        await OfferStoredInstallNowAsync(admin, true, registered.Id);

        var offer = await ClientUpdateOfHeartbeatAsync(await ReportAsync(pc, registered, new { clientVersion = "0.19.0" }));

        Assert.True(offer!.Value.GetProperty("installNow").GetBoolean());
    }

    [Fact]
    public async Task Report_WithoutATarget_AnswersAnEmptyObject()
    {
        var (app, admin, pc, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await AddPcAsync(app, "PC-01");

        var response = await ReportAsync(pc, registered, new { clientVersion = "0.19.0" });

        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Report_ReportingTheTargetVersion_ClearsTheTarget_AndOffersNothing()
    {
        var (app, admin, pc, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        var response = await ReportAsync(pc, registered, new { clientVersion = "0.20.0" });

        Assert.Null(await ClientUpdateOfHeartbeatAsync(response));
        Assert.Null(await ClientUpdateOfListEntryAsync(admin, "PC-01"));
        Assert.Equal("0.20.0", (await StoredPcAsync(app, registered.Id)).ClientVersion);
    }

    [Fact]
    public async Task Report_WithATargetThatIsNotTheStoredRelease_OffersNothing()
    {
        var (app, admin, pc, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await AddPcAsync(app, "PC-01");
        await SetTargetAsync(app, registered.Id, "0.19.0");

        var response = await ReportAsync(pc, registered, new { clientVersion = "0.18.0" });

        Assert.Null(await ClientUpdateOfHeartbeatAsync(response));
        Assert.Null(await ClientUpdateOfListEntryAsync(admin, "PC-01"));
    }

    [Fact]
    public async Task Report_RecordsTheUpdateProgress_ForTheTarget()
    {
        var (app, admin, pc, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        await ReportAsync(pc, registered, new { update = new { version = "0.20.0", state = "waiting", error = (string?)null } });

        var entry = await ClientUpdateOfListEntryAsync(admin, "PC-01");
        Assert.Equal("waiting", entry!.Value.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Report_RecordsAFailure_WithItsError()
    {
        var (app, admin, pc, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        await ReportAsync(pc, registered, new { update = new { version = "0.20.0", state = "failed", error = "The download didn't match." } });

        var entry = await ClientUpdateOfListEntryAsync(admin, "PC-01");
        Assert.Equal("failed", entry!.Value.GetProperty("state").GetString());
        Assert.Equal("The download didn't match.", entry.Value.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Report_IgnoresProgressForAnotherVersion()
    {
        var (app, admin, pc, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        await ReportAsync(pc, registered, new { update = new { version = "0.19.0", state = "failed", error = "old" } });

        Assert.Equal("pending", (await ClientUpdateOfListEntryAsync(admin, "PC-01"))!.Value.GetProperty("state").GetString());
    }

    // Identity and refusals

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer not-a-registered-token")]
    public async Task Report_WithMissingMalformedOrUnknownToken_Returns401(string? authorization)
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        await AddPcAsync(app, "PC-01");
        using var request = new HttpRequestMessage(HttpMethod.Post, ServiceReportRoute) { Content = JsonContent.Create(FullReport()) };
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        var response = await pc.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(NotRegisteredMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(0, await ReportCountAsync(app));
    }

    [Fact]
    public async Task Report_WithARemovedPcToken_Returns401_AndSavesNothing()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");
        await app.WithDbAsync(async db =>
        {
            db.Pcs.RemoveRange(db.Pcs);
            await db.SaveChangesAsync();
            return 0;
        });

        var response = await ReportAsync(pc, registered, FullReport());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await ReportCountAsync(app));
        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{")]
    public async Task Report_WithABodyThatIsNotAJsonObject_Returns400(string json)
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");

        var response = await RawReportAsync(pc, registered.Token, new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(0, await ReportCountAsync(app));
    }

    [Fact]
    public async Task Report_WithoutAJsonContentType_Returns400()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");

        var response = await RawReportAsync(pc, registered.Token, new StringContent("{}", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Report_AcceptsABodyWellAboveTheHeartbeatLimit()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");
        var json = JsonSerializer.Serialize(FullReport());
        var padding = new string(' ', 16 * 1024 - json.Length);

        var response = await RawReportAsync(pc, registered.Token, new StringContent(json + padding, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Report_OverThe32KBLimit_Returns413_AndSavesNothing()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");
        var json = JsonSerializer.Serialize(FullReport());
        var padding = new string(' ', ChannelGuard.MaxServiceReportBodyBytes + 1 - json.Length);

        var response = await RawReportAsync(pc, registered.Token, new StringContent(json + padding, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, await ReportCountAsync(app));
    }

    [Fact]
    public async Task Report_IsLimitedTo6PerMinutePerAddress()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app, "PC-01");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < ClientEndpoints.MaxServiceReportsPerMinute + 1; i++)
        {
            statuses.Add((await ReportAsync(pc, registered, FullReport())).StatusCode);
        }

        Assert.All(statuses.Take(ClientEndpoints.MaxServiceReportsPerMinute), status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact]
    public async Task Report_OnTheAdminPort_IsNotFound()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var registered = await AddPcAsync(app, "PC-01");

        var response = await PostAsync(admin, ServiceReportRoute, registered.Token, JsonContent.Create(FullReport()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static long InstallerBytesLength(string version) => Installer(version).Length;
}
