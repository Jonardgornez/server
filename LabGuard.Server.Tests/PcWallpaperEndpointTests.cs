using System.Net;
using System.Text;
using System.Text.Json;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;
using static LabGuard.Server.Tests.WallpaperTestSupport;

namespace LabGuard.Server.Tests;

// The administrator gives PCs a wallpaper, or removes it. The change is recorded for every listed PC, offline ones too, and
// a wallpaper that no PC uses any more is removed.
public class PcWallpaperEndpointTests
{
    // Every admin route refuses anonymous callers, the PC port, and any request without a JSON body.

    [Fact]
    public async Task AssignRoute_WithoutCookie_Returns401()
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();
        var pc = await AddPcAsync(app);

        var response = await AssignAsync(browser, null, pc.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AssignRoute_FromThePcPort_Returns404()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        var id = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));

        var response = await AssignAsync(pcClient, id, pc.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(await AssignedWallpaperIdAsync(app, pc.Id));
    }

    [Theory]
    [InlineData("text/plain", "{}")]
    [InlineData("application/x-www-form-urlencoded", "")]
    [InlineData("application/json", "")]
    [InlineData("application/json", "null")]
    [InlineData("application/json", "{not json")]
    public async Task AssignRoute_WithoutAJsonBody_Returns400AndChangesNothing(string contentType, string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var id = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));

        using var content = new StringContent(body, Encoding.UTF8, contentType);
        var response = await admin.PostAsync(AssignPath, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, await ErrorOfAsync(response));
        Assert.Null(await AssignedWallpaperIdAsync(app, pc.Id));
    }

    // Each body is refused: a missing, null or empty list, a wrong type, or no wallpaperId. A body that leaves out
    // wallpaperId is never read as a removal.
    [Theory]
    [InlineData("""{"wallpaperId":1}""")]
    [InlineData("""{"ids":null,"wallpaperId":1}""")]
    [InlineData("""{"ids":[],"wallpaperId":1}""")]
    [InlineData("""{"ids":[1]}""")]
    [InlineData("""{"ids":[1],"wallpaperId":"one"}""")]
    public async Task AssignRoute_InvalidBody_Returns400AndChangesNothing(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var id = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));
        await AssignAsync(admin, id, pc.Id);

        var response = await admin.PostAsync(AssignPath, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, await ErrorOfAsync(response));
        Assert.Equal(id, await AssignedWallpaperIdAsync(app, pc.Id));
    }

    [Fact]
    public async Task AssignRoute_MoreThanTheLimit_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        // One over the server's limit of 1000 PCs per request.
        var ids = Enumerable.Range(1, 1001).ToArray();

        var response = await AssignAsync(admin, null, ids);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AssignRoute_UnknownWallpaperId_Returns404AndChangesNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var response = await AssignAsync(admin, 999, pc.Id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(NotFoundMessage, await ErrorOfAsync(response));
        Assert.Null(await AssignedWallpaperIdAsync(app, pc.Id));
    }

    // Assigning

    [Fact]
    public async Task Assign_SetsTheWallpaperOnEveryListedPc_IncludingOfflineOnes()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var online = await AddPcAsync(app, "PC-01");
        var offline = await AddPcAsync(app, "PC-02", seenAgo: TimeSpan.FromMinutes(5));
        var notListed = await AddPcAsync(app, "PC-03");
        var id = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));

        var response = await AssignAsync(admin, id, online.Id, offline.Id);

        Assert.Equal(2, await AffectedAsync(response));
        Assert.Equal(id, await AssignedWallpaperIdAsync(app, online.Id));
        Assert.Equal(id, await AssignedWallpaperIdAsync(app, offline.Id));
        Assert.Null(await AssignedWallpaperIdAsync(app, notListed.Id));
    }

    [Fact]
    public async Task Assign_UnknownPcIds_AreSkipped_AndNotCounted()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var id = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));

        var response = await AssignAsync(admin, id, pc.Id, 999);

        Assert.Equal(1, await AffectedAsync(response));
        Assert.Equal(id, await AssignedWallpaperIdAsync(app, pc.Id));
    }

    [Fact]
    public async Task Assign_ReplacesTheWallpaper_AndRemovesTheOldOneNoPcUses()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var first = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));
        var second = await UploadIdAsync(admin, BackgroundTestImages.Png(32, 32));
        await AssignAsync(admin, first, pc.Id);

        await AssignAsync(admin, second, pc.Id);

        Assert.Equal(second, await AssignedWallpaperIdAsync(app, pc.Id));
        Assert.False(await WallpaperStoredAsync(app, first));
        Assert.True(await WallpaperStoredAsync(app, second));
    }

    [Fact]
    public async Task Assign_SharedWallpaper_IsKeptWhileAnyPcStillUsesIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var first = await AddPcAsync(app, "PC-01");
        var second = await AddPcAsync(app, "PC-02");
        var shared = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));
        var replacement = await UploadIdAsync(admin, BackgroundTestImages.Png(32, 32));
        await AssignAsync(admin, shared, first.Id, second.Id);

        await AssignAsync(admin, replacement, first.Id);
        Assert.True(await WallpaperStoredAsync(app, shared));

        await AssignAsync(admin, replacement, second.Id);
        Assert.False(await WallpaperStoredAsync(app, shared));
    }

    [Fact]
    public async Task Assign_KeepsAnUploadThatNoPcHasBeenGivenYet()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var waiting = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));
        var given = await UploadIdAsync(admin, BackgroundTestImages.Png(32, 32));

        await AssignAsync(admin, given, pc.Id);

        Assert.True(await WallpaperStoredAsync(app, waiting));
    }

    [Fact]
    public async Task Assign_AlsoRemovesAnUploadThatIsOlderThanAnHour()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var stale = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));
        var given = await UploadIdAsync(admin, BackgroundTestImages.Png(32, 32));
        app.Time.Advance(TimeSpan.FromMinutes(61));
        using var later = await SignedInAsync(app);

        var response = await AssignAsync(later, given, pc.Id);

        Assert.Equal(1, await AffectedAsync(response));
        Assert.False(await WallpaperStoredAsync(app, stale));
        Assert.True(await WallpaperStoredAsync(app, given));
    }

    // Removing

    [Fact]
    public async Task Remove_NullClearsTheWallpaper_OnEveryListedPc()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var first = await AddPcAsync(app, "PC-01");
        var second = await AddPcAsync(app, "PC-02");
        var id = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));
        await AssignAsync(admin, id, first.Id, second.Id);

        var response = await AssignAsync(admin, null, first.Id, second.Id);

        Assert.Equal(2, await AffectedAsync(response));
        Assert.Null(await AssignedWallpaperIdAsync(app, first.Id));
        Assert.Null(await AssignedWallpaperIdAsync(app, second.Id));
    }

    [Fact]
    public async Task Remove_DeletesTheWallpaperThatNoPcUsesAnymore()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var id = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));
        await AssignAsync(admin, id, pc.Id);

        await AssignAsync(admin, null, pc.Id);

        Assert.False(await WallpaperStoredAsync(app, id));
        Assert.Equal(HttpStatusCode.NotFound, (await PreviewAsync(admin, id)).StatusCode);
    }

    [Fact]
    public async Task Remove_ForAPcWithNoWallpaper_IsASuccessWithNothingToDelete()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var response = await AssignAsync(admin, null, pc.Id);

        Assert.Equal(1, await AffectedAsync(response));
        Assert.Null(await AssignedWallpaperIdAsync(app, pc.Id));
    }

    // Removing a PC

    [Fact]
    public async Task DeletePc_RemovesItsWallpaper_WhenNoOtherPcUsesIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-01", seenAgo: TimeSpan.FromMinutes(5));
        var id = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));
        await AssignAsync(admin, id, pc.Id);

        var response = await admin.DeleteAsync($"/api/pcs/{pc.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await WallpaperStoredAsync(app, id));
    }

    [Fact]
    public async Task DeletePc_KeepsAWallpaperAnotherPcStillUses()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var removed = await AddPcAsync(app, "PC-01", seenAgo: TimeSpan.FromMinutes(5));
        var other = await AddPcAsync(app, "PC-02");
        var id = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));
        await AssignAsync(admin, id, removed.Id, other.Id);

        var response = await admin.DeleteAsync($"/api/pcs/{removed.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(await WallpaperStoredAsync(app, id));
        Assert.Equal(id, await AssignedWallpaperIdAsync(app, other.Id));
    }

    // The PC list

    [Fact]
    public async Task List_ShowsTheWallpaperIdAndHash_OrNull()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var bytes = BackgroundTestImages.Png(16, 16);
        var assignedPc = await AddPcAsync(app, "PC-01");
        await AddPcAsync(app, "PC-02");
        var id = await UploadIdAsync(admin, bytes);
        await AssignAsync(admin, id, assignedPc.Id);

        var assigned = await ListEntryAsync(admin, "PC-01");
        var none = await ListEntryAsync(admin, "PC-02");

        var wallpaper = assigned.GetProperty("wallpaper");
        Assert.Equal(id, wallpaper.GetProperty("id").GetInt32());
        Assert.Equal(Sha256Of(bytes), wallpaper.GetProperty("sha256").GetString());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("wallpaper").ValueKind);
    }

    [Fact]
    public async Task List_WallpaperCarriesOnlyIdAndHash_NeverTheImageBytes()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-01");
        var id = await UploadIdAsync(admin, BackgroundTestImages.Png(16, 16));
        await AssignAsync(admin, id, pc.Id);

        var wallpaper = (await ListEntryAsync(admin, "PC-01")).GetProperty("wallpaper");

        Assert.Equal(new[] { "id", "sha256" }, wallpaper.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
    }
}
