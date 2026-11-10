using System.Net;
using LabGuard.Server.ClientUpdates;
using LabGuard.Server.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static LabGuard.Server.Tests.ClientUpdateTestSupport;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The PC's download of the stored installer. It is streamed only to the PC the release is offered to, and only for its hash.
public class ClientUpdateDownloadEndpointTests
{
    private const string NotOfferedMessage = "No client update is offered to this PC.";
    private const string UnknownHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static async Task<(TestApp App, HttpClient Admin, FakeInstallerInspector Inspector)> AppWithReleaseAsync()
    {
        var inspector = new FakeInstallerInspector();
        var app = new TestApp(configureServices: services =>
        {
            services.RemoveAll<IInstallerInspector>();
            services.AddSingleton<IInstallerInspector>(inspector);
        });
        var admin = await SignedInAsync(app);
        return (app, admin, inspector);
    }

    [Fact]
    public async Task Download_StreamsTheStoredBytes_WithTheLengthAndSafetyHeaders()
    {
        var (app, admin, inspector) = await AppWithReleaseAsync();
        using var _app = app;
        using var _admin = admin;
        var sha256 = await StoredHashAsync(admin, inspector, "0.20.0");
        var pc = await AddPcAsync(app, "PC-01");
        await OfferStoredAsync(admin, pc.Id);
        using var client = app.CreatePcClient(OtherPcAddress);

        var response = await DownloadAsync(client, sha256, pc.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = Installer("0.20.0");
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Download_WithoutAKnownToken_IsUnauthorized()
    {
        var (app, admin, inspector) = await AppWithReleaseAsync();
        using var _app = app;
        using var _admin = admin;
        var sha256 = await StoredHashAsync(admin, inspector, "0.20.0");
        using var client = app.CreatePcClient(OtherPcAddress);

        Assert.Equal(HttpStatusCode.Unauthorized, (await DownloadAsync(client, sha256, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DownloadAsync(client, sha256, "not-a-real-token")).StatusCode);
    }

    [Theory]
    [InlineData("ABCDEF0000000000000000000000000000000000000000000000000000000000")]
    [InlineData("00000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000%0A")]
    [InlineData("../../../etc/passwd")]
    public async Task Download_WithAMalformedHash_IsNotFound_BeforeTheTokenIsChecked(string hash)
    {
        var (app, admin, _) = await AppWithReleaseAsync();
        using var _app = app;
        using var _admin = admin;
        using var client = app.CreatePcClient(OtherPcAddress);

        var response = await DownloadAsync(client, hash, "not-a-real-token");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_ForAPcWithNoTarget_IsNotFound()
    {
        var (app, admin, inspector) = await AppWithReleaseAsync();
        using var _app = app;
        using var _admin = admin;
        var sha256 = await StoredHashAsync(admin, inspector, "0.20.0");
        var offered = await AddPcAsync(app, "PC-01");
        var notOffered = await AddPcAsync(app, "PC-02");
        await OfferStoredAsync(admin, offered.Id);
        using var client = app.CreatePcClient(OtherPcAddress);

        var response = await DownloadAsync(client, sha256, notOffered.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(NotOfferedMessage, (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Download_AfterTheTargetIsCancelled_IsNotFound()
    {
        var (app, admin, inspector) = await AppWithReleaseAsync();
        using var _app = app;
        using var _admin = admin;
        var sha256 = await StoredHashAsync(admin, inspector, "0.20.0");
        var pc = await AddPcAsync(app, "PC-01");
        await OfferStoredAsync(admin, pc.Id);
        await CancelAsync(admin, pc.Id);
        using var client = app.CreatePcClient(OtherPcAddress);

        var response = await DownloadAsync(client, sha256, pc.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_WithAHashThatIsNotTheStoredRelease_IsNotFound()
    {
        var (app, admin, inspector) = await AppWithReleaseAsync();
        using var _app = app;
        using var _admin = admin;
        await StoredHashAsync(admin, inspector, "0.20.0");
        var pc = await AddPcAsync(app, "PC-01");
        await OfferStoredAsync(admin, pc.Id);
        using var client = app.CreatePcClient(OtherPcAddress);

        var response = await DownloadAsync(client, UnknownHash, pc.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_WithNoReleaseStored_IsNotFound()
    {
        var (app, admin, _) = await AppWithReleaseAsync();
        using var _app = app;
        using var _admin = admin;
        var pc = await AddPcAsync(app, "PC-01");
        using var client = app.CreatePcClient(OtherPcAddress);

        var response = await DownloadAsync(client, UnknownHash, pc.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_OfTheReplacedRelease_IsNotFound()
    {
        var (app, admin, inspector) = await AppWithReleaseAsync();
        using var _app = app;
        using var _admin = admin;
        var oldHash = await StoredHashAsync(admin, inspector, "0.20.0");
        var pc = await AddPcAsync(app, "PC-01");
        await OfferStoredAsync(admin, pc.Id);
        await StoredHashAsync(admin, inspector, "0.21.0");
        using var client = app.CreatePcClient(OtherPcAddress);

        var response = await DownloadAsync(client, oldHash, pc.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_IsRateLimitedToSixPerMinutePerAddress()
    {
        var (app, admin, _) = await AppWithReleaseAsync();
        using var _app = app;
        using var _admin = admin;
        using var client = app.CreatePcClient(OtherPcAddress);
        using var otherClient = app.CreatePcClient("203.0.113.9");

        for (var i = 0; i < ClientEndpoints.MaxClientUpdateDownloadsPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await DownloadAsync(client, UnknownHash, "not-a-real-token")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await DownloadAsync(client, UnknownHash, "not-a-real-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DownloadAsync(otherClient, UnknownHash, "not-a-real-token")).StatusCode);
    }
}
