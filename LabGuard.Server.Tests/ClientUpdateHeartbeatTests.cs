using System.Net;
using System.Text.Json;
using LabGuard.Server.ClientUpdates;
using LabGuard.Server.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static LabGuard.Server.Tests.ClientUpdateTestSupport;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The update fields of the heartbeat: the offer back to the PC, and what the PC reports about its version and update.
public class ClientUpdateHeartbeatTests
{
    private static async Task<(TestApp App, HttpClient Admin, HttpClient Pc, FakeInstallerInspector Inspector, string Sha256)> AppWithReleaseAsync(string version = "0.20.0")
    {
        var inspector = new FakeInstallerInspector();
        var app = new TestApp(configureServices: services =>
        {
            services.RemoveAll<IInstallerInspector>();
            services.AddSingleton<IInstallerInspector>(inspector);
        });
        var admin = await SignedInAsync(app);
        var release = await StoreReleaseAsync(admin, inspector, version);
        var pc = app.CreatePcClient(OtherPcAddress);
        return (app, admin, pc, inspector, release.GetProperty("sha256").GetString()!);
    }

    // Offers the stored release to the PC, so its target is set.
    private static async Task<TestPc> TargetedPcAsync(TestApp app, HttpClient admin, string name = "PC-01")
    {
        var pc = await AddPcAsync(app, name);
        Assert.Equal(1, await AffectedAsync(await OfferStoredAsync(admin, pc.Id)));
        return pc;
    }

    private static async Task<JsonElement> StateOfAsync(HttpClient admin, string name) =>
        (await ClientUpdateOfListEntryAsync(admin, name))!.Value;

    // Offers

    [Fact]
    public async Task Heartbeat_OffersTheStoredRelease_WhenTheTargetMatchesAndThePcIsBehind()
    {
        var (app, admin, pc, _, sha256) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        var response = await HeartbeatWithAsync(pc, registered, new { clientVersion = "0.19.0" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var offer = await ClientUpdateOfHeartbeatAsync(response);
        Assert.NotNull(offer);
        Assert.Equal("0.20.0", offer!.Value.GetProperty("version").GetString());
        Assert.Equal(sha256, offer.Value.GetProperty("sha256").GetString());
        Assert.Equal(InstallerBytesLength("0.20.0"), offer.Value.GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task Heartbeat_OffersWhenTheReportedVersionIsUnknown()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        var response = await HeartbeatWithAsync(pc, registered, new { });

        Assert.NotNull(await ClientUpdateOfHeartbeatAsync(response));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Heartbeat_OffersTheInstallNowChoice_OfTheUpdate(bool installNow)
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await AddPcAsync(app, "PC-01");
        await OfferStoredInstallNowAsync(admin, installNow, registered.Id);

        var response = await HeartbeatWithAsync(pc, registered, new { clientVersion = "0.19.0" });

        var offer = await ClientUpdateOfHeartbeatAsync(response);
        Assert.Equal(installNow, offer!.Value.GetProperty("installNow").GetBoolean());
    }

    [Fact]
    public async Task Heartbeat_ReportingTheTargetVersion_ClearsTheInstallNowChoice()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await AddPcAsync(app, "PC-01");
        await OfferStoredInstallNowAsync(admin, true, registered.Id);

        await HeartbeatWithAsync(pc, registered, new { clientVersion = "0.20.0" });

        var stored = await StoredPcAsync(app, registered.Id);
        Assert.Null(stored.ClientUpdateTargetVersion);
        Assert.False(stored.ClientUpdateInstallNow);
    }

    [Fact]
    public async Task Heartbeat_WithoutATarget_LeavesTheOfferOut()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await AddPcAsync(app, "PC-01");

        var response = await HeartbeatWithAsync(pc, registered, new { clientVersion = "0.19.0" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await ClientUpdateOfHeartbeatAsync(response));
    }

    [Fact]
    public async Task Heartbeat_WithATargetThatIsNotTheStoredRelease_OffersNothing()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await AddPcAsync(app, "PC-01");
        await SetTargetAsync(app, registered.Id, "0.19.0");

        var response = await HeartbeatWithAsync(pc, registered, new { clientVersion = "0.18.0" });

        Assert.Null(await ClientUpdateOfHeartbeatAsync(response));
        Assert.Null(await ClientUpdateOfListEntryAsync(admin, "PC-01"));
    }

    [Fact]
    public async Task Heartbeat_ReportingTheTargetVersion_ClearsTheTarget_AndOffersNothing()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        var response = await HeartbeatWithAsync(pc, registered, new { clientVersion = "0.20.0" });

        Assert.Null(await ClientUpdateOfHeartbeatAsync(response));
        Assert.Null(await ClientUpdateOfListEntryAsync(admin, "PC-01"));
        Assert.Equal("0.20.0", (await StoredPcAsync(app, registered.Id)).ClientVersion);
    }

    [Fact]
    public async Task Heartbeat_ReportingAHigherVersion_ClearsTheTarget()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        await HeartbeatWithAsync(pc, registered, new { clientVersion = "0.21.0" });

        Assert.Null(await ClientUpdateOfListEntryAsync(admin, "PC-01"));
    }

    [Fact]
    public async Task Heartbeat_FromAnOlderClient_WithNoUpdateFields_StillAnswers_AndKeepsTheTarget()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        var response = await HeartbeatWithAsync(pc, registered, new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await ClientUpdateOfHeartbeatAsync(response));
        Assert.NotNull(await ClientUpdateOfListEntryAsync(admin, "PC-01"));
    }

    // Progress

    [Fact]
    public async Task Heartbeat_RecordsTheProgressOfTheUpdate_AndWhenItChanged()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);
        app.Time.Advance(TimeSpan.FromMinutes(2));

        var response = await HeartbeatWithAsync(pc, registered, new { clientUpdate = new { state = "downloading", version = "0.20.0", error = (string?)null } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = await StateOfAsync(admin, "PC-01");
        Assert.Equal("downloading", entry.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("error").ValueKind);
        var stored = await StoredPcAsync(app, registered.Id);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.ClientUpdateStateAtUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Heartbeat_RecordsAFailure_WithItsError()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        await HeartbeatWithAsync(pc, registered, new { clientUpdate = new { state = "failed", version = "0.20.0", error = "The download didn't match." } });

        var entry = await StateOfAsync(admin, "PC-01");
        Assert.Equal("failed", entry.GetProperty("state").GetString());
        Assert.Equal("The download didn't match.", entry.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Heartbeat_IgnoresAReportForAnotherVersion()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        await HeartbeatWithAsync(pc, registered, new { clientUpdate = new { state = "failed", version = "0.19.0", error = "old" } });

        var entry = await StateOfAsync(admin, "PC-01");
        Assert.Equal("pending", entry.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("error").ValueKind);
    }

    [Theory]
    [InlineData("rebooting")]
    [InlineData("")]
    [InlineData("FAILED")]
    public async Task Heartbeat_IgnoresAnUnknownState(string state)
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        var response = await HeartbeatWithAsync(pc, registered, new { clientUpdate = new { state, version = "0.20.0", error = "x" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pending", (await StateOfAsync(admin, "PC-01")).GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("0.20")]
    [InlineData("v0.20.0")]
    [InlineData("0.20.0\n")]
    [InlineData("2147483648.0.0")]
    public async Task Heartbeat_IgnoresAnUnreadableReportedVersion(string version)
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        await HeartbeatWithAsync(pc, registered, new { clientUpdate = new { state = "failed", version, error = "x" } });

        Assert.Equal("pending", (await StateOfAsync(admin, "PC-01")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Heartbeat_CleansTheError_TrimsIt_AndDropsControlAndBidiCharacters()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        await HeartbeatWithAsync(pc, registered, new
        {
            clientUpdate = new { state = "failed", version = "0.20.0", error = "  Disk\u202E full\u0000\n now\u200F\u2066 " },
        });

        Assert.Equal("Disk full now", (await StateOfAsync(admin, "PC-01")).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Heartbeat_DropsC1ControlsAndInvisibleFormattingCharacters()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        await HeartbeatWithAsync(pc, registered, new
        {
            clientUpdate = new
            {
                state = "failed",
                version = "0.20.0",
                error = "a\u0085b\u009Fc\u200Bd\u200De\u2028f\u2029g\u061Ch\uFEFFi",
            },
        });

        Assert.Equal("abcdefghi", (await StateOfAsync(admin, "PC-01")).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Heartbeat_CapsTheErrorAt200Characters()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        await HeartbeatWithAsync(pc, registered, new { clientUpdate = new { state = "failed", version = "0.20.0", error = new string('x', 300) } });

        Assert.Equal(200, (await StateOfAsync(admin, "PC-01")).GetProperty("error").GetString()!.Length);
    }

    [Fact]
    public async Task Heartbeat_CapNeverSplitsASurrogatePair()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);
        // 199 characters, then an emoji: its high surrogate is the 200th character, so the cap must stop before it.
        var error = new string('x', 199) + "\uD83D\uDE00";

        await HeartbeatWithAsync(pc, registered, new { clientUpdate = new { state = "failed", version = "0.20.0", error } });

        Assert.Equal(new string('x', 199), (await StateOfAsync(admin, "PC-01")).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Heartbeat_StoresNoError_WhenNothingPrintableIsLeft()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        await HeartbeatWithAsync(pc, registered, new { clientUpdate = new { state = "failed", version = "0.20.0", error = "\n\u200E \t" } });

        Assert.Equal(JsonValueKind.Null, (await StateOfAsync(admin, "PC-01")).GetProperty("error").ValueKind);
    }

    // Lenient reading

    [Theory]
    [InlineData("0.20")]
    [InlineData("v0.20.0")]
    [InlineData("0.20.0\n")]
    [InlineData("0.20.0.1")]
    public async Task Heartbeat_IgnoresAnInvalidClientVersion(string version)
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await AddPcAsync(app, "PC-01");

        var response = await HeartbeatWithAsync(pc, registered, new { clientVersion = version });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await StoredPcAsync(app, registered.Id)).ClientVersion);
    }

    [Fact]
    public async Task Heartbeat_ReportingALowerVersion_ReplacesTheStoredVersion()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await AddPcAsync(app, "PC-01");

        await HeartbeatWithAsync(pc, registered, new { clientVersion = "0.22.3" });
        await HeartbeatWithAsync(pc, registered, new { clientVersion = "0.20.5" });

        Assert.Equal("0.20.5", (await StoredPcAsync(app, registered.Id)).ClientVersion);
    }

    [Fact]
    public async Task Heartbeat_WithoutAVersion_MakesTheStoredVersionUnknown()
    {
        // A client older than 0.20.0 reports no version. It must not keep showing the newer client that ran before it.
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await AddPcAsync(app, "PC-01");

        await HeartbeatWithAsync(pc, registered, new { clientVersion = "0.22.3" });
        await HeartbeatWithAsync(pc, registered, new { });

        Assert.Null((await StoredPcAsync(app, registered.Id)).ClientVersion);
    }

    [Fact]
    public async Task Heartbeat_WithWronglyTypedUpdateFields_StillAnswers200_AndStoresNothing()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var registered = await TargetedPcAsync(app, admin);

        var response = await HeartbeatWithRawBodyAsync(pc, registered, "{\"clientVersion\":5,\"clientUpdate\":\"oops\"}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await StoredPcAsync(app, registered.Id);
        Assert.Null(stored.ClientVersion);
        Assert.Null(stored.ClientUpdateState);
    }

    [Fact]
    public async Task Heartbeat_WithAWronglyTypedVersion_StillKeepsTheSession()
    {
        var (app, admin, pc, _, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        await AddAccountAsync(app);
        var registered = await AddPcAsync(app, "PC-01");
        var sessionId = await LoggedInSessionIdAsync(pc, registered);

        var response = await HeartbeatWithAsync(pc, registered, new { sessionId, clientVersion = 5 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await JsonOf(response)).GetProperty("session").ValueKind);
    }

    private static long InstallerBytesLength(string version) => Installer(version).Length;
}
