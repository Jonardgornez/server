using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using LabGuard.Server.Backgrounds;
using LabGuard.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The administrator's background images and the PCs' download of them. The image is one per kind (login, lock), shared by every PC.
public class BackgroundEndpointTests
{
    private const string Route = "/api/backgrounds";
    private const string NotSetMessage = "No background is set for this screen.";
    private const string UnknownKindMessage = "Unknown screen background.";
    private const string InvalidImageMessage = "Choose a PNG or JPEG image.";
    private const string TooLargeMessage = "The image must be 5 MB or smaller.";
    private const string TooManyPixelsMessage = "The image must be 7680 × 4320 pixels (8K) or smaller.";

    private static ByteArrayContent ImageContent(byte[] bytes, string contentType = "image/png")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return content;
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string kind, byte[] bytes, string contentType = "image/png") =>
        client.PutAsync($"{Route}/{kind}", ImageContent(bytes, contentType));

    private static Task<HttpResponseMessage> ImageAsync(HttpClient client, string kind) =>
        client.GetAsync($"{Route}/{kind}/image");

    private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<JsonElement> ErrorOf(HttpResponseMessage response)
    {
        var body = await JsonOf(response);
        return body.GetProperty("error");
    }

    private static async Task<JsonElement> ListOfAsync(HttpClient admin) =>
        await JsonOf(await admin.GetAsync(Route));

    private static async Task<bool> StoredAsync(TestApp app, string kind) =>
        await app.WithDbAsync(db => db.ScreenBackgrounds.AnyAsync(b => b.Kind == kind));

    private static Task<HttpResponseMessage> PcDownloadAsync(HttpClient pc, string kind, string? token) =>
        PcDownloadWithIfNoneMatchAsync(pc, kind, token, ifNoneMatch: null);

    private static Task<HttpResponseMessage> PcDownloadWithIfNoneMatchAsync(HttpClient pc, string kind, string? token, string? ifNoneMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/client/backgrounds/{kind}");
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return pc.SendAsync(request);
    }

    // Stands in for a second upload that stores the same kind after this upload has read the table and found it empty.
    private sealed class CompetingFirstWriteInterceptor : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context is { } context && context.ChangeTracker.Entries<ScreenBackground>().Any(e => e.State == EntityState.Added))
            {
                Fired = true;
                await using var connection = new SqliteConnection(context.Database.GetConnectionString());
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO ScreenBackgrounds (Kind, ContentType, Data, Sha256, UpdatedAtUtc) VALUES ('login', 'image/png', x'00', @sha, '2026-01-01 00:00:00')";
                command.Parameters.AddWithValue("@sha", new string('0', 64));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return result;
        }
    }

    // Listing

    [Fact]
    public async Task List_WithNothingSet_ReturnsNullForBothKinds_AndIsNotCached()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await admin.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await JsonOf(response);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("login").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lock").ValueKind);
    }

    // Upload, read back and replace

    [Fact]
    public async Task Put_ThenListAndImage_RoundTripTheBytesAndHash()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var bytes = BackgroundTestImages.Png(1920, 1080, 4096);

        var put = await PutAsync(admin, "login", bytes);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var info = await JsonOf(put);
        Assert.Equal("image/png", info.GetProperty("contentType").GetString());
        Assert.Equal(bytes.Length, info.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(Sha256Of(bytes), info.GetProperty("sha256").GetString());
        Assert.Equal(app.Time.GetUtcNow(), DateTimeOffset.Parse(info.GetProperty("updatedAtUtc").GetString()!));

        var listed = (await ListOfAsync(admin)).GetProperty("login");
        Assert.Equal(Sha256Of(bytes), listed.GetProperty("sha256").GetString());
        Assert.Equal(bytes.Length, listed.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, (await ListOfAsync(admin)).GetProperty("lock").ValueKind);

        var image = await ImageAsync(admin, "login");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await image.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", image.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("inline", image.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("default-src 'none'; sandbox", image.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(image.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Put_Jpeg_IsStoredAsJpeg()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var bytes = BackgroundTestImages.Jpeg(800, 600);

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(admin, "lock", bytes, "image/jpeg")).StatusCode);

        var image = await ImageAsync(admin, "lock");
        Assert.Equal("image/jpeg", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await image.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Put_Again_ReplacesTheImage_AndKeepsOneRow()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var first = BackgroundTestImages.Png(10, 10);
        var second = BackgroundTestImages.Png(20, 20);
        await PutAsync(admin, "login", first);

        var replaced = await PutAsync(admin, "login", second);

        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var listed = (await ListOfAsync(admin)).GetProperty("login");
        Assert.Equal(Sha256Of(second), listed.GetProperty("sha256").GetString());
        var image = await ImageAsync(admin, "login");
        Assert.Equal(second, await image.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, await app.WithDbAsync(db => db.ScreenBackgrounds.CountAsync()));
    }

    // Delete

    [Fact]
    public async Task Delete_RemovesTheImage_AndIsIdempotent()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await PutAsync(admin, "login", BackgroundTestImages.Png());

        var first = await admin.DeleteAsync($"{Route}/login");
        var second = await admin.DeleteAsync($"{Route}/login");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ListOfAsync(admin)).GetProperty("login").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await ImageAsync(admin, "login")).StatusCode);
    }

    // Unknown kinds and missing images

    [Fact]
    public async Task ImageWithNoneSet_IsNotFoundWithAnError()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await ImageAsync(admin, "login");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(NotSetMessage, (await ErrorOf(response)).GetString());
    }

    [Theory]
    [InlineData("wallpaper")]
    [InlineData("LOGIN")]
    public async Task UnknownKind_IsNotFound_OnEveryRoute(string kind)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var image = await ImageAsync(admin, kind);
        var put = await PutAsync(admin, kind, BackgroundTestImages.Png());
        var delete = await admin.DeleteAsync($"{Route}/{kind}");

        Assert.Equal(HttpStatusCode.NotFound, image.StatusCode);
        Assert.Equal(UnknownKindMessage, (await ErrorOf(image)).GetString());
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.False(await StoredAsync(app, kind));
    }

    // Refused uploads

    [Theory]
    [InlineData("image/png", "jpeg")]
    [InlineData("image/jpeg", "png")]
    public async Task Put_BytesThatDoNotMatchTheDeclaredType_IsRejected(string declared, string actual)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var bytes = actual == "png" ? BackgroundTestImages.Png() : BackgroundTestImages.Jpeg();

        var response = await PutAsync(admin, "login", bytes, declared);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidImageMessage, (await ErrorOf(response)).GetString());
        Assert.False(await StoredAsync(app, "login"));
    }

    [Fact]
    public async Task Put_GarbageBytes_IsRejected()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutAsync(admin, "login", "not an image at all"u8.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidImageMessage, (await ErrorOf(response)).GetString());
        Assert.False(await StoredAsync(app, "login"));
    }

    [Fact]
    public async Task Put_EmptyBody_IsRejected()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutAsync(admin, "login", []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidImageMessage, (await ErrorOf(response)).GetString());
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("text/plain")]
    public async Task Put_UnsupportedContentType_IsRejectedEvenWithAValidPng(string contentType)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutAsync(admin, "login", BackgroundTestImages.Png(), contentType);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidImageMessage, (await ErrorOf(response)).GetString());
        Assert.False(await StoredAsync(app, "login"));
    }

    [Fact]
    public async Task Put_JpegWithoutAReadableSize_IsRejected()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutAsync(admin, "login", BackgroundTestImages.JpegWithoutFrame(), "image/jpeg");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidImageMessage, (await ErrorOf(response)).GetString());
    }

    [Fact]
    public async Task Put_ImageWiderThan8192Pixels_IsRejected()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutAsync(admin, "login", BackgroundTestImages.Png(8193, 100));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(TooManyPixelsMessage, (await ErrorOf(response)).GetString());
        Assert.False(await StoredAsync(app, "login"));
    }

    [Fact]
    public async Task Put_ImageWithMoreTotalPixelsThan8K_IsRejectedEvenWithinEachSide()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutAsync(admin, "login", BackgroundTestImages.Png(7680, 4321));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(TooManyPixelsMessage, (await ErrorOf(response)).GetString());
        Assert.False(await StoredAsync(app, "login"));
    }

    [Fact]
    public async Task Put_Image8KFrame_IsStored()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutAsync(admin, "login", BackgroundTestImages.Png(7680, 4320));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Put_PngWhoseWidthDoesNotFitAnInt_IsRejectedAsInvalid()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        // -1 is 0xFFFFFFFF as the unsigned width in the header, which must not wrap around to a small size.
        var response = await PutAsync(admin, "login", BackgroundTestImages.Png(-1, 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidImageMessage, (await ErrorOf(response)).GetString());
        Assert.False(await StoredAsync(app, "login"));
    }

    // Two first uploads for the same kind race: the other one stores the row after this one has looked and found none.
    [Fact]
    public async Task Put_WhenAnotherUploadStoresTheSameKindFirst_StillSucceeds()
    {
        var competitor = new CompetingFirstWriteInterceptor();
        using var app = new TestApp(configureServices: services =>
        {
            services.AddSingleton<ISaveChangesInterceptor>(competitor);
            // EF Core takes interceptors from the options, not from DI, so the test adds this one to the context's options.
            services.AddDbContext<AppDbContext>((provider, options) => options.AddInterceptors(provider.GetRequiredService<ISaveChangesInterceptor>()));
        });
        using var admin = await SignedInAsync(app);
        var bytes = BackgroundTestImages.Png(16, 16);

        var response = await PutAsync(admin, "login", bytes);

        Assert.True(competitor.Fired);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await (await ImageAsync(admin, "login")).Content.ReadAsByteArrayAsync());
        Assert.Equal(1, await app.WithDbAsync(db => db.ScreenBackgrounds.CountAsync()));
    }

    [Fact]
    public async Task Put_ExactlyFiveMegabytes_IsStored()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutAsync(admin, "login", BackgroundTestImages.Png(totalLength: BackgroundTestImages.FiveMegabytes));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(BackgroundTestImages.FiveMegabytes, (await JsonOf(response)).GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task Put_OneByteOverFiveMegabytes_IsTooLarge()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutAsync(admin, "login", BackgroundTestImages.Png(totalLength: BackgroundTestImages.FiveMegabytes + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(TooLargeMessage, (await ErrorOf(response)).GetString());
        Assert.False(await StoredAsync(app, "login"));
    }

    [Fact]
    public async Task Put_OverTheLimitWithoutAContentLength_IsStillRefused()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var bytes = BackgroundTestImages.Png(totalLength: BackgroundTestImages.FiveMegabytes + 1);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{Route}/login")
        {
            Content = new StreamContent(new MemoryStream(bytes)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        var response = await admin.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.False(await StoredAsync(app, "login"));
    }

    // Authentication

    [Fact]
    public async Task Anonymous_CannotListReadUploadOrDelete()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await PutAsync(admin, "login", BackgroundTestImages.Png());
        using var anonymous = app.CreateBrowser();

        var list = await anonymous.GetAsync(Route);
        var image = await ImageAsync(anonymous, "login");
        var put = await PutAsync(anonymous, "lock", BackgroundTestImages.Png());
        var delete = await anonymous.DeleteAsync($"{Route}/login");

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, image.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
        Assert.False(await StoredAsync(app, "lock"));
        Assert.True(await StoredAsync(app, "login"));
    }

    // The PC's download

    [Fact]
    public async Task Pc_Download_ReturnsTheBytes_WithTheHashHeader()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var bytes = BackgroundTestImages.Png(64, 64);
        await PutAsync(admin, "lock", bytes);
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app);

        var response = await PcDownloadAsync(pc, "lock", registered.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(Sha256Of(bytes), response.Headers.GetValues("X-Background-Sha256").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-real-token")]
    public async Task Pc_DownloadWithoutAKnownToken_IsUnauthorized(string? token)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await PutAsync(admin, "login", BackgroundTestImages.Png());
        using var pc = app.CreatePcClient(OtherPcAddress);

        var response = await PcDownloadAsync(pc, "login", token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Pc_Download_SendsTheHashAsETag_AndAMatchingIfNoneMatchGetsNotModified()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var bytes = BackgroundTestImages.Png(64, 64);
        await PutAsync(admin, "login", bytes);
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app);
        var etag = $"\"{Sha256Of(bytes)}\"";

        var first = await PcDownloadAsync(pc, "login", registered.Token);
        var revalidated = await PcDownloadWithIfNoneMatchAsync(pc, "login", registered.Token, etag);
        var stale = await PcDownloadWithIfNoneMatchAsync(pc, "login", registered.Token, "\"0000\"");

        Assert.Equal(etag, first.Headers.ETag?.Tag);
        Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
        Assert.Empty(await revalidated.Content.ReadAsByteArrayAsync());
        Assert.Equal(etag, revalidated.Headers.ETag?.Tag);
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Equal(bytes, await stale.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Pc_Download_AllowsTenPerMinutePerAddress_ThenRefusesWithTooManyRequests()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        using var otherPc = app.CreatePcClient("203.0.113.9");

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await PcDownloadAsync(pc, "login", "not-a-token")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await PcDownloadAsync(pc, "login", "not-a-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PcDownloadAsync(otherPc, "login", "not-a-token")).StatusCode);
    }

    [Fact]
    public async Task Pc_DownloadWhenNotSet_IsNotFound()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app);

        var response = await PcDownloadAsync(pc, "login", registered.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(NotSetMessage, (await ErrorOf(response)).GetString());
    }

    // The heartbeat's hashes

    [Fact]
    public async Task Heartbeat_OmitsBackgrounds_WhenNeitherIsSet()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app);

        var body = await HeartbeatBodyAsync(pc, registered);

        Assert.False(body.TryGetProperty("backgrounds", out _));
    }

    [Fact]
    public async Task Heartbeat_CarriesOnlyTheHashesOfImagesThatAreSet()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app);
        var bytes = BackgroundTestImages.Png(16, 16);
        await PutAsync(admin, "login", bytes);

        var body = await HeartbeatBodyAsync(pc, registered);

        var backgrounds = body.GetProperty("backgrounds");
        Assert.Equal(Sha256Of(bytes), backgrounds.GetProperty("login").GetString());
        Assert.Equal(JsonValueKind.Null, backgrounds.GetProperty("lock").ValueKind);
    }

    [Fact]
    public async Task Heartbeat_CarriesBothHashes_WhenBothAreSet()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app);
        var login = BackgroundTestImages.Png(16, 16);
        var lockImage = BackgroundTestImages.Jpeg(32, 32);
        await PutAsync(admin, "login", login);
        await PutAsync(admin, "lock", lockImage, "image/jpeg");

        var backgrounds = (await HeartbeatBodyAsync(pc, registered)).GetProperty("backgrounds");

        Assert.Equal(Sha256Of(login), backgrounds.GetProperty("login").GetString());
        Assert.Equal(Sha256Of(lockImage), backgrounds.GetProperty("lock").GetString());
    }
}
