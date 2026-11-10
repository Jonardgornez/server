using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LabGuard.Server.ClientUpdates;
using LabGuard.Server.Data;
using LabGuard.Server.Pcs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static LabGuard.Server.Tests.ClientUpdateTestSupport;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// Offering the stored installer to chosen PCs, cancelling it, and what the PC list shows about it.
public class ClientUpdateRolloutEndpointTests
{
    private const string ReleaseChangedMessage = "The uploaded update changed. Check the version and try again.";

    private static async Task<(TestApp App, HttpClient Admin, FakeInstallerInspector Inspector)> AppWithReleaseAsync(string version = "0.20.0")
    {
        var inspector = new FakeInstallerInspector();
        var app = new TestApp(configureServices: services =>
        {
            services.RemoveAll<IInstallerInspector>();
            services.AddSingleton<IInstallerInspector>(inspector);
        });
        var admin = await SignedInAsync(app);
        await StoreReleaseAsync(admin, inspector, version);
        return (app, admin, inspector);
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response) =>
        (await JsonOf(response)).GetProperty("error").GetString()!;

    // Offer

    [Fact]
    public async Task Offer_WithoutAnInstaller_IsConflict()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-01");

        var response = await OfferAsync(admin, "0.20.0", pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(NoReleaseMessage, await ErrorOf(response));
        Assert.Null(await ClientUpdateOfListEntryAsync(admin, "PC-01"));
    }

    [Fact]
    public async Task Offer_WithAVersionThatIsNotTheStoredOne_IsConflict_AndTouchesNothing()
    {
        var (app, admin, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var pc = await AddPcAsync(app, "PC-01");

        var response = await OfferAsync(admin, "0.21.0", pc.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ReleaseChangedMessage, await ErrorOf(response));
        Assert.Null(await ClientUpdateOfListEntryAsync(admin, "PC-01"));
    }

    [Theory]
    [InlineData("0.20")]
    [InlineData("v0.20.0")]
    [InlineData("")]
    public async Task Offer_WithAVersionThatIsNotMajorMinorPatch_IsBadRequest(string version)
    {
        var (app, admin, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var pc = await AddPcAsync(app, "PC-01");

        var response = await OfferAsync(admin, version, pc.Id);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, await ErrorOf(response));
    }

    [Fact]
    public async Task Offer_WithoutAVersion_IsBadRequest()
    {
        var (app, admin, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var pc = await AddPcAsync(app, "PC-01");

        var response = await admin.PostAsJsonAsync(OfferRoute, new { pcIds = new[] { pc.Id } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await ClientUpdateOfListEntryAsync(admin, "PC-01"));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("duplicate")]
    [InlineData("too many")]
    public async Task Offer_WithBadPcIds_IsBadRequest(string kind)
    {
        var (app, admin, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var pc = await AddPcAsync(app, "PC-01");
        int[] ids = kind switch
        {
            "empty" => [],
            "zero" => [0],
            "negative" => [-1],
            "duplicate" => [pc.Id, pc.Id],
            _ => Enumerable.Range(1, PcEndpoints.MaxClientUpdatePcIds + 1).ToArray(),
        };

        var response = await OfferAsync(admin, "0.20.0", ids);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, await ErrorOf(response));
        Assert.Null(await ClientUpdateOfListEntryAsync(admin, "PC-01"));
    }

    [Fact]
    public async Task Offer_SetsTheTarget_OnlyForPcsWithAnUnknownOrLowerVersion()
    {
        var (app, admin, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var unknown = await AddPcAsync(app, "PC-unknown");
        var older = await AddPcAsync(app, "PC-older");
        var same = await AddPcAsync(app, "PC-same");
        var newer = await AddPcAsync(app, "PC-newer");
        await SetClientVersionAsync(app, older.Id, "0.19.0");
        await SetClientVersionAsync(app, same.Id, "0.20.0");
        await SetClientVersionAsync(app, newer.Id, "0.21.0");

        // 999 is not a PC: it is simply not affected.
        var response = await OfferStoredAsync(admin, unknown.Id, older.Id, same.Id, newer.Id, 999);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await JsonOf(response)).GetProperty("affected").GetInt32());
        var offered = await ClientUpdateOfListEntryAsync(admin, "PC-unknown");
        Assert.Equal("0.20.0", offered!.Value.GetProperty("targetVersion").GetString());
        Assert.Equal("pending", offered.Value.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, offered.Value.GetProperty("error").ValueKind);
        Assert.NotNull(await ClientUpdateOfListEntryAsync(admin, "PC-older"));
        Assert.Null(await ClientUpdateOfListEntryAsync(admin, "PC-same"));
        Assert.Null(await ClientUpdateOfListEntryAsync(admin, "PC-newer"));
    }

    [Fact]
    public async Task Offer_ComparesVersionsAsNumbers()
    {
        var (app, admin, _) = await AppWithReleaseAsync("0.10.0");
        using var _app = app;
        using var _admin = admin;
        var pc = await AddPcAsync(app, "PC-01");
        await SetClientVersionAsync(app, pc.Id, "0.9.0");

        var response = await OfferStoredAsync(admin, pc.Id);

        Assert.Equal(1, await AffectedAsync(response));
    }

    [Fact]
    public async Task Offer_ReachesAnOfflinePc()
    {
        var (app, admin, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var pc = await AddPcAsync(app, "PC-01", seenAgo: TimeSpan.FromHours(1));

        var response = await OfferStoredAsync(admin, pc.Id);

        Assert.Equal(1, await AffectedAsync(response));
        Assert.NotNull(await ClientUpdateOfListEntryAsync(admin, "PC-01"));
    }

    [Fact]
    public async Task Offer_ResetsThePreviousProgress()
    {
        var (app, admin, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var pc = await AddPcAsync(app, "PC-01");
        await OfferStoredAsync(admin, pc.Id);
        await app.WithDbAsync(db => db.Pcs.Where(p => p.Id == pc.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(p => p.ClientUpdateState, "failed")
            .SetProperty(p => p.ClientUpdateError, "Hash mismatch")));

        await OfferStoredAsync(admin, pc.Id);

        var entry = await ClientUpdateOfListEntryAsync(admin, "PC-01");
        Assert.Equal("pending", entry!.Value.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, entry.Value.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task Offer_Logs_WhatWasOfferedAndToHowMany()
    {
        var (app, admin, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var first = await AddPcAsync(app, "PC-01");
        var second = await AddPcAsync(app, "PC-02");

        await OfferStoredAsync(admin, first.Id, second.Id);

        Assert.Contains(app.Logs.Entries, entry => entry.Contains("Client update 0.20.0 offered to 2 PC(s) by tester.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OfferAndCancel_WithoutSignIn_AreUnauthorized()
    {
        var (app, admin, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var pc = await AddPcAsync(app, "PC-01");
        using var anonymous = app.CreateBrowser();

        var offer = await OfferAsync(anonymous, "0.20.0", pc.Id);
        var cancel = await CancelAsync(anonymous, pc.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, offer.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, cancel.StatusCode);
    }

    // Cancel

    [Theory]
    [InlineData(null)]
    [InlineData("downloading")]
    [InlineData("installing")]
    [InlineData("failed")]
    public async Task Cancel_ClearsTheTargetAndTheState_WhateverTheState(string? state)
    {
        var (app, admin, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var pc = await AddPcAsync(app, "PC-01");
        await OfferStoredAsync(admin, pc.Id);
        await app.WithDbAsync(db => db.Pcs.Where(p => p.Id == pc.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(p => p.ClientUpdateState, state)
            .SetProperty(p => p.ClientUpdateError, state == "failed" ? "Download failed" : null)));

        var response = await CancelAsync(admin, pc.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await ClientUpdateOfListEntryAsync(admin, "PC-01"));
        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Null(stored.ClientUpdateTargetVersion);
        Assert.Null(stored.ClientUpdateState);
        Assert.Null(stored.ClientUpdateError);
        Assert.Null(stored.ClientUpdateStateAtUtc);
    }

    [Fact]
    public async Task Cancel_IsIdempotent_AndCountsOnlyWhatItChanged()
    {
        var (app, admin, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var pc = await AddPcAsync(app, "PC-01");
        await OfferStoredAsync(admin, pc.Id);

        var first = await CancelAsync(admin, pc.Id);
        var second = await CancelAsync(admin, pc.Id);

        Assert.Equal(1, await AffectedAsync(first));
        Assert.Equal(0, await AffectedAsync(second));
    }

    [Fact]
    public async Task Cancel_LeavesOtherPcsAlone()
    {
        var (app, admin, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var cancelled = await AddPcAsync(app, "PC-01");
        var kept = await AddPcAsync(app, "PC-02");
        await OfferStoredAsync(admin, cancelled.Id, kept.Id);

        await CancelAsync(admin, cancelled.Id);

        Assert.Null(await ClientUpdateOfListEntryAsync(admin, "PC-01"));
        Assert.NotNull(await ClientUpdateOfListEntryAsync(admin, "PC-02"));
    }

    [Fact]
    public async Task Cancel_WithBadPcIds_IsBadRequest()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await CancelAsync(admin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // What the PC list shows

    [Fact]
    public async Task List_ShowsTheReportedVersion_AndTheUpdateAsTheAdministratorSeesIt()
    {
        var (app, admin, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        var pc = await AddPcAsync(app, "PC-01");
        await SetClientVersionAsync(app, pc.Id, "0.19.0");
        await OfferStoredAsync(admin, pc.Id);
        await app.WithDbAsync(db => db.Pcs.Where(p => p.Id == pc.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(p => p.ClientUpdateState, "installing")
            .SetProperty(p => p.ClientUpdateError, (string?)null)));

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.Equal("0.19.0", entry.GetProperty("clientVersion").GetString());
        var update = entry.GetProperty("clientUpdate");
        Assert.Equal("0.20.0", update.GetProperty("targetVersion").GetString());
        Assert.Equal("installing", update.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, update.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task List_ShowsNullsForAPcThatNeverReported_AndHasNoUpdate()
    {
        var (app, admin, _) = await AppWithReleaseAsync("0.20.0");
        using var _app = app;
        using var _admin = admin;
        await AddPcAsync(app, "PC-01");

        var entry = await ListEntryAsync(admin, "PC-01");

        Assert.Equal(JsonValueKind.Null, entry.GetProperty("clientVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("clientUpdate").ValueKind);
    }
}
