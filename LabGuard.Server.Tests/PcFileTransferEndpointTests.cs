using System.Net;
using System.Net.Http.Json;
using System.Text;
using LabGuard.Server.Pcs;
using Microsoft.EntityFrameworkCore;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;
using static LabGuard.Server.Tests.SharedFileTestSupport;

namespace LabGuard.Server.Tests;

// The administrator sends an uploaded file to PCs, follows the transfers, and decides what happens when the file already exists.
public class PcFileTransferEndpointTests
{
    // Access

    [Theory]
    [InlineData("POST", "/api/pcs/files")]
    [InlineData("GET", "/api/pcs/files/transfers?ids=1")]
    [InlineData("POST", "/api/pcs/files/transfers/decision")]
    public async Task AdminRoutes_WithoutCookie_Return401(string method, string path)
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();

        var response = await browser.SendAsync(new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Send_FromThePcPort_Returns404()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var fileId = await UploadIdAsync(admin, Bytes("abc"));

        var response = await SendAsync(pcClient, new { ids = new[] { pc.Id }, sharedFileId = fileId });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await app.WithDbAsync(db => db.PcFileTransfers.CountAsync()));
    }

    // Sending

    [Fact]
    public async Task Send_CreatesOneWaitingTransferPerPc_WithTheDefaultLocation()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var first = await AddPcAsync(app, "PC-01");
        var second = await AddPcAsync(app, "PC-02", seenAgo: TimeSpan.FromHours(1));
        var bytes = Bytes("hello");
        var fileId = await UploadIdAsync(admin, bytes, "notes.pdf");

        var transfers = await SendTransfersAsync(admin, fileId, first.Id, second.Id);

        Assert.Equal(2, transfers.Length);
        Assert.Equal([first.Id, second.Id], transfers.Select(t => t.GetProperty("pcId").GetInt32()));
        Assert.Equal(["PC-01", "PC-02"], transfers.Select(t => t.GetProperty("pcName").GetString()));
        foreach (var transfer in transfers)
        {
            Assert.True(transfer.GetProperty("id").GetInt64() > 0);
            Assert.Equal("notes.pdf", transfer.GetProperty("fileName").GetString());
            Assert.Equal(bytes.Length, transfer.GetProperty("sizeBytes").GetInt64());
            Assert.Equal("desktop", transfer.GetProperty("folder").GetString());
            Assert.Equal("Lykantra Share", transfer.GetProperty("subfolder").GetString());
            Assert.Equal("waiting", transfer.GetProperty("status").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, transfer.GetProperty("decision").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, transfer.GetProperty("savedAs").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, transfer.GetProperty("error").ValueKind);
            Assert.True(transfer.TryGetProperty("createdUtc", out _));
            Assert.True(transfer.TryGetProperty("updatedUtc", out _));
        }
    }

    [Fact]
    public async Task Send_UsesTheChosenFolder_AndNormalizesTheSubfolder()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var fileId = await UploadIdAsync(admin, Bytes("abc"));

        var response = await SendAsync(admin, new { ids = new[] { pc.Id }, sharedFileId = fileId, folder = "documents", subfolder = "Class 7/Week 2" });

        var transfer = (await TransfersOfAsync(response)).Single();
        Assert.Equal("documents", transfer.GetProperty("folder").GetString());
        Assert.Equal("Class 7\\Week 2", transfer.GetProperty("subfolder").GetString());
    }

    [Fact]
    public async Task Send_EmptySubfolder_SavesStraightIntoTheFolder()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var fileId = await UploadIdAsync(admin, Bytes("abc"));

        var response = await SendAsync(admin, new { ids = new[] { pc.Id }, sharedFileId = fileId, folder = "downloads", subfolder = "" });

        Assert.Equal("", (await TransfersOfAsync(response)).Single().GetProperty("subfolder").GetString());
    }

    [Fact]
    public async Task Send_RepeatedPcId_CreatesOneTransfer()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var fileId = await UploadIdAsync(admin, Bytes("abc"));

        var transfers = await SendTransfersAsync(admin, fileId, pc.Id, pc.Id);

        Assert.Single(transfers);
    }

    [Fact]
    public async Task Send_UnknownPcId_Returns400AndCreatesNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var fileId = await UploadIdAsync(admin, Bytes("abc"));

        var response = await SendAsync(admin, new { ids = new[] { pc.Id, 999 }, sharedFileId = fileId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await app.WithDbAsync(db => db.PcFileTransfers.CountAsync()));
    }

    [Fact]
    public async Task Send_UnknownSharedFile_Returns404()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        var response = await SendAsync(admin, new { ids = new[] { pc.Id }, sharedFileId = 999 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("File not found.", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Send_RefreshesTheSharedFileTime_SoTheCleanupKeepsIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var fileId = await UploadIdAsync(admin, Bytes("abc"));
        app.Time.Advance(TimeSpan.FromMinutes(50));
        // Signed in again: the first sign-in has passed its idle limit.
        using var laterAdmin = await SignedInAsync(app);

        await SendTransfersAsync(laterAdmin, fileId, pc.Id);

        var stored = await app.WithDbAsync(db => db.SharedFiles.AsNoTracking().SingleAsync());
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.CreatedUtc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Send_MoreThan200Pcs_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var fileId = await UploadIdAsync(admin, Bytes("abc"));

        var response = await SendAsync(admin, new { ids = Enumerable.Range(1, 201).ToArray(), sharedFileId = fileId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"sharedFileId":1}""")]
    [InlineData("""{"ids":[],"sharedFileId":1}""")]
    [InlineData("""{"ids":[1]}""")]
    [InlineData("""{"ids":[1],"sharedFileId":"one"}""")]
    [InlineData("null")]
    public async Task Send_InvalidBody_Returns400(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await AddPcAsync(app);

        var response = await admin.PostAsync(SendRoute, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("pictures", null)]
    [InlineData("Desktop", null)]
    [InlineData("desktop", "C:\\Windows")]
    [InlineData("desktop", "..\\..\\Windows")]
    [InlineData("desktop", "a/b/c/d")]
    [InlineData("desktop", "CON")]
    public async Task Send_BadLocation_Returns400AndCreatesNothing(string folder, string? subfolder)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var fileId = await UploadIdAsync(admin, Bytes("abc"));

        var response = await SendAsync(admin, new { ids = new[] { pc.Id }, sharedFileId = fileId, folder, subfolder });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await app.WithDbAsync(db => db.PcFileTransfers.CountAsync()));
    }

    // Following transfers

    [Fact]
    public async Task Transfers_ReturnsTheListedTransfers_AndSkipsUnknownIds()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var saved = await SeedTransferAsync(app, pc.Id, "saved");
        var failed = await SeedTransferAsync(app, pc.Id, "failed");
        await SeedTransferAsync(app, pc.Id, "waiting");

        var transfers = await TransfersOfAsync(await admin.GetAsync($"{TransfersRoute}?ids={saved},{failed},999"));

        Assert.Equal([saved, failed], transfers.Select(t => t.GetProperty("id").GetInt64()));
        Assert.Equal(["saved", "failed"], transfers.Select(t => t.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Transfers_WaitingForAnHour_ShowsAsExpired()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, "waiting", createdUtc: app.Time.GetUtcNow() - TimeSpan.FromHours(1));

        var transfer = (await TransfersOfAsync(await admin.GetAsync($"{TransfersRoute}?ids={id}"))).Single();

        Assert.Equal("expired", transfer.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?ids=")]
    [InlineData("?ids=abc")]
    [InlineData("?ids=1,,2")]
    [InlineData("?ids=-1")]
    [InlineData("?ids=0")]
    public async Task Transfers_InvalidIds_Return400(string query)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await admin.GetAsync($"{TransfersRoute}{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("1", new long[] { 1 })]
    [InlineData("3, 1,3", new long[] { 3, 1 })]
    public void ParseTransferIds_ReadsDistinctPositiveIds(string raw, long[] expected) =>
        Assert.Equal(expected, PcEndpoints.ParseTransferIds(raw));

    [Fact]
    public void ParseTransferIds_RefusesMoreThan200() =>
        Assert.Null(PcEndpoints.ParseTransferIds(string.Join(',', Enumerable.Range(1, 201))));

    // Decisions

    [Theory]
    [InlineData("overwrite")]
    [InlineData("keepBoth")]
    [InlineData("skip")]
    public async Task Decision_RecordsOnExistsTransfers_AndLeavesOthersUnchanged(string decision)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var exists = await SeedTransferAsync(app, pc.Id, "exists");
        var saved = await SeedTransferAsync(app, pc.Id, "saved");
        var downloading = await SeedTransferAsync(app, pc.Id, "downloading");

        var transfers = await TransfersOfAsync(await DecideAsync(admin, decision, exists, saved, downloading));

        Assert.Equal(3, transfers.Length);
        Assert.Equal(decision, transfers.Single(t => t.GetProperty("id").GetInt64() == exists).GetProperty("decision").GetString());
        Assert.Equal("exists", (await StoredTransferAsync(app, exists)).Status);
        Assert.Equal(decision, (await StoredTransferAsync(app, exists)).Decision);
        Assert.Null((await StoredTransferAsync(app, saved)).Decision);
        Assert.Null((await StoredTransferAsync(app, downloading)).Decision);
    }

    [Theory]
    [InlineData("""{"transferIds":[1],"decision":"delete"}""")]
    [InlineData("""{"transferIds":[1],"decision":"keepboth"}""")]
    [InlineData("""{"transferIds":[1]}""")]
    [InlineData("""{"transferIds":[],"decision":"skip"}""")]
    [InlineData("""{"decision":"skip"}""")]
    public async Task Decision_InvalidBody_Returns400AndChangesNothing(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var exists = await SeedTransferAsync(app, pc.Id, "exists");

        var response = await admin.PostAsync(DecisionRoute, new StringContent(body.Replace("[1]", $"[{exists}]"), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await StoredTransferAsync(app, exists)).Decision);
    }

    // Removing a PC

    [Fact]
    public async Task DeletingAPc_RemovesItsTransfers_AndKeepsOtherPcs()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var removed = await AddPcAsync(app, "PC-01", seenAgo: TimeSpan.FromMinutes(5));
        var kept = await AddPcAsync(app, "PC-02");
        var removedTransfer = await SeedTransferAsync(app, removed.Id, "waiting");
        var keptTransfer = await SeedTransferAsync(app, kept.Id, "waiting");

        var response = await admin.DeleteAsync($"/api/pcs/{removed.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await TransferExistsAsync(app, removedTransfer));
        Assert.True(await TransferExistsAsync(app, keptTransfer));
    }
}
