using System.Net;
using LabGuard.Server.Clients;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;
using static LabGuard.Server.Tests.WallpaperTestSupport;

namespace LabGuard.Server.Tests;

// The administrator's wallpaper uploads and previews, and the PC's download of its own wallpaper.
public class WallpaperEndpointTests
{
    // Upload

    [Fact]
    public async Task Upload_ValidPng_ReturnsTheStoredDescription()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var bytes = BackgroundTestImages.Png(1920, 1080, 4096);

        var response = await UploadAsync(admin, bytes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal("image/png", body.GetProperty("contentType").GetString());
        Assert.Equal(bytes.Length, body.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(Sha256Of(bytes), body.GetProperty("sha256").GetString());
        Assert.True(body.GetProperty("id").GetInt32() > 0);
    }

    [Fact]
    public async Task Upload_ValidJpeg_IsStoredAsJpeg()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, BackgroundTestImages.Jpeg(800, 600), "image/jpeg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", (await JsonOf(response)).GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task Upload_TheSameBytesTwice_ReturnsTheSameRow()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var bytes = BackgroundTestImages.Png(64, 64);

        var first = await UploadIdAsync(admin, bytes);
        var second = await UploadIdAsync(admin, bytes);

        Assert.Equal(first, second);
        Assert.Equal(1, await WallpaperCountAsync(app));
    }

    [Fact]
    public async Task Upload_DifferentBytes_StoreSeparateRows()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var first = await UploadIdAsync(admin, BackgroundTestImages.Png(64, 64));
        var second = await UploadIdAsync(admin, BackgroundTestImages.Png(65, 64));

        Assert.NotEqual(first, second);
        Assert.Equal(2, await WallpaperCountAsync(app));
    }

    [Theory]
    [InlineData("image/png", "jpeg")]
    [InlineData("image/jpeg", "png")]
    public async Task Upload_BytesThatDoNotMatchTheDeclaredType_IsRejected(string declared, string actual)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var bytes = actual == "png" ? BackgroundTestImages.Png() : BackgroundTestImages.Jpeg();

        var response = await UploadAsync(admin, bytes, declared);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidImageMessage, await ErrorOfAsync(response));
        Assert.Equal(0, await WallpaperCountAsync(app));
    }

    [Fact]
    public async Task Upload_GarbageBytes_IsRejected()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, "not an image at all"u8.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidImageMessage, await ErrorOfAsync(response));
        Assert.Equal(0, await WallpaperCountAsync(app));
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("text/plain")]
    public async Task Upload_UnsupportedContentType_IsRejectedEvenWithAValidPng(string contentType)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, BackgroundTestImages.Png(), contentType);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidImageMessage, await ErrorOfAsync(response));
        Assert.Equal(0, await WallpaperCountAsync(app));
    }

    [Fact]
    public async Task Upload_ImageWiderThan8192Pixels_IsRejected()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, BackgroundTestImages.Png(8193, 100));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(TooManyPixelsMessage, await ErrorOfAsync(response));
        Assert.Equal(0, await WallpaperCountAsync(app));
    }

    [Fact]
    public async Task Upload_OneByteOverFiveMegabytes_IsTooLarge()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, BackgroundTestImages.Png(totalLength: BackgroundTestImages.FiveMegabytes + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(TooLargeMessage, await ErrorOfAsync(response));
        Assert.Equal(0, await WallpaperCountAsync(app));
    }

    [Fact]
    public async Task Upload_ExactlyFiveMegabytes_IsStored()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, BackgroundTestImages.Png(totalLength: BackgroundTestImages.FiveMegabytes));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Preview

    [Fact]
    public async Task Preview_ReturnsTheBytes_AsNoStore_WithTheImageHeaders()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var bytes = BackgroundTestImages.Png(40, 30);
        var id = await UploadIdAsync(admin, bytes);

        var response = await PreviewAsync(admin, id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("default-src 'none'; sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Preview_UnknownId_IsNotFound()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PreviewAsync(admin, 999);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(NotFoundMessage, await ErrorOfAsync(response));
    }

    // Authentication

    [Fact]
    public async Task Anonymous_CannotUploadOrPreview()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var id = await UploadIdAsync(admin, BackgroundTestImages.Png());
        using var anonymous = app.CreateBrowser();

        var upload = await UploadAsync(anonymous, BackgroundTestImages.Png(2, 2));
        var preview = await PreviewAsync(anonymous, id);

        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, preview.StatusCode);
        Assert.Equal(1, await WallpaperCountAsync(app));
    }

    [Fact]
    public async Task AdminRoutes_FromThePcPort_Return404()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var id = await UploadIdAsync(admin, BackgroundTestImages.Png());
        using var pcClient = app.CreatePcClient(OtherPcAddress);

        var upload = await UploadAsync(pcClient, BackgroundTestImages.Png(2, 2));
        var preview = await PreviewAsync(pcClient, id);

        Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        Assert.Equal(1, await WallpaperCountAsync(app));
    }

    // Uploads that no PC is given are removed once they are an hour old

    // The admin session is idle-limited, so after the clock moves the administrator signs in again.
    [Fact]
    public async Task UnassignedUpload_OlderThanAnHour_IsRemovedByTheNextUpload()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var stale = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));
        app.Time.Advance(TimeSpan.FromMinutes(61));
        using var later = await SignedInAsync(app);

        var fresh = await UploadIdAsync(later, BackgroundTestImages.Png(32, 32));

        Assert.False(await WallpaperStoredAsync(app, stale));
        Assert.True(await WallpaperStoredAsync(app, fresh));
        Assert.Equal(HttpStatusCode.NotFound, (await PreviewAsync(later, stale)).StatusCode);
    }

    [Fact]
    public async Task ReuploadOfTheSameImage_RestartsItsHour_SoTheNextCleanupKeepsIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var first = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));
        app.Time.Advance(TimeSpan.FromMinutes(59));
        using var again = await SignedInAsync(app);
        var reused = await UploadIdAsync(again, BackgroundTestImages.Png(16, 16));
        app.Time.Advance(TimeSpan.FromMinutes(2));
        using var later = await SignedInAsync(app);

        await UploadIdAsync(later, BackgroundTestImages.Png(32, 32));

        Assert.Equal(first, reused);
        Assert.True(await WallpaperStoredAsync(app, first));
    }

    [Fact]
    public async Task UnassignedUpload_JustUnderAnHour_IsKept()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var kept = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));
        app.Time.Advance(TimeSpan.FromMinutes(59));
        using var later = await SignedInAsync(app);

        await UploadIdAsync(later, BackgroundTestImages.Png(32, 32));

        Assert.True(await WallpaperStoredAsync(app, kept));
    }

    // The PC's download

    [Fact]
    public async Task PcDownload_ReturnsOnlyItsOwnWallpaper()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        var first = await AddPcAsync(app, "PC-01");
        var second = await AddPcAsync(app, "PC-02");
        var firstBytes = BackgroundTestImages.Png(16, 16);
        var secondBytes = BackgroundTestImages.Jpeg(32, 32);
        var firstId = await UploadIdAsync(admin, firstBytes);
        var secondId = await UploadIdAsync(admin, secondBytes, "image/jpeg");
        await AssignAsync(admin, firstId, first.Id);
        await AssignAsync(admin, secondId, second.Id);

        var firstResponse = await PcDownloadAsync(pcClient, first.Token);
        var secondResponse = await PcDownloadAsync(pcClient, second.Token);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(firstBytes, await firstResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", firstResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(secondBytes, await secondResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", secondResponse.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task PcDownload_IsNeverCached()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await AssignAsync(admin, await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16)), pc.Id);

        var response = await PcDownloadAsync(pcClient, pc.Token);

        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task PcDownload_WhenNoWallpaperIsAssigned_IsNotFound()
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);

        var response = await PcDownloadAsync(pcClient, pc.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(NotSetMessage, await ErrorOfAsync(response));
    }

    [Fact]
    public async Task PcDownload_WhenTheWallpaperWasRemoved_IsNotFound()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await AssignAsync(admin, await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16)), pc.Id);
        await AssignAsync(admin, null, pc.Id);

        var response = await PcDownloadAsync(pcClient, pc.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-real-token")]
    public async Task PcDownload_WithoutAKnownToken_IsUnauthorized(string? token)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        await AssignAsync(admin, await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16)), pc.Id);

        var response = await PcDownloadAsync(pcClient, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PcDownload_CannotReachAnotherWallpaperById()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        var otherId = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));

        // There is no route with an id, so the path matches nothing and no image can come back, with or without the PC's token.
        using var byId = new HttpRequestMessage(HttpMethod.Get, $"{PcDownloadPath}/{otherId}");
        using var byIdWithToken = new HttpRequestMessage(HttpMethod.Get, $"{PcDownloadPath}/{otherId}");
        byIdWithToken.Headers.TryAddWithoutValidation("Authorization", $"Bearer {pc.Token}");

        var anonymous = await pcClient.SendAsync(byId);
        var withToken = await pcClient.SendAsync(byIdWithToken);

        Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, withToken.StatusCode);
        Assert.NotEqual("image/png", withToken.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task PcDownload_CannotReachTheAdminRoutes()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        var id = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));
        await AssignAsync(admin, id, pc.Id);

        var preview = await PreviewAsync(pcClient, id);
        var listed = await pcClient.GetAsync("/api/pcs");

        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, listed.StatusCode);
    }

    [Fact]
    public async Task PcDownload_FromTheAdminPort_IsNotFound()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var response = await PcDownloadAsync(admin, pc.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PcDownload_AllowsTenPerMinutePerAddress_ThenRefusesWithTooManyRequests()
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        using var otherPcClient = app.CreatePcClient("203.0.113.9");

        for (var i = 0; i < ClientEndpoints.MaxWallpaperDownloadsPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await PcDownloadAsync(pcClient, "not-a-token")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await PcDownloadAsync(pcClient, "not-a-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PcDownloadAsync(otherPcClient, "not-a-token")).StatusCode);
    }
}
