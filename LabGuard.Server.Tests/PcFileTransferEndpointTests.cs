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
    [InlineData("POST", "/api/pcs/files/transfers/cancel")]
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
            Assert.Equal(System.Text.Json.JsonValueKind.Null, transfer.GetProperty("busyWith").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, transfer.GetProperty("pcClientVersion").ValueKind);
        }

        // The first PC is online but has reported no client version; the second has been offline for an hour.
        Assert.Equal(["clientTooOld", "offline"], transfers.Select(t => t.GetProperty("waitingReason").GetString()));
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

    // Why a waiting transfer hasn't been handed over

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("0.25.9")]
    public async Task Transfers_WaitingOnAnOnlinePcWithAnOldClient_IsClientTooOld(string? clientVersion)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        await SetClientVersionAsync(app, pc.Id, clientVersion);
        var id = await SeedTransferAsync(app, pc.Id, "waiting");

        var transfer = await TransferAsync(admin, id);

        Assert.Equal("clientTooOld", transfer.GetProperty("waitingReason").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, transfer.GetProperty("busyWith").ValueKind);
        Assert.Equal(clientVersion, transfer.GetProperty("pcClientVersion").GetString());
    }

    // The dashboard's rule: a PC not seen for 15 seconds is offline. That comes first, even with an old client or a busy PC.
    [Theory]
    [InlineData(15, "0.26.0", false, "offline")]
    [InlineData(15, null, true, "offline")]
    [InlineData(14, "0.26.0", false, null)]
    [InlineData(14, null, true, "clientTooOld")]
    [InlineData(14, "0.27.0", true, "busy")]
    public async Task Transfers_WaitingReason_FollowsThePcsState(int seenSecondsAgo, string? clientVersion, bool isBusy, string? expected)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, seenAgo: TimeSpan.FromSeconds(seenSecondsAgo));
        await SetClientVersionAsync(app, pc.Id, clientVersion);
        if (isBusy)
        {
            await SeedTransferAsync(app, pc.Id, "downloading", fileName: "lesson.txt");
        }

        var id = await SeedTransferAsync(app, pc.Id, "waiting");

        var transfer = await TransferAsync(admin, id);

        Assert.Equal(expected, transfer.GetProperty("waitingReason").GetString());
        Assert.Equal(expected == "busy" ? "lesson.txt" : null, transfer.GetProperty("busyWith").GetString());
        Assert.Equal(clientVersion, transfer.GetProperty("pcClientVersion").GetString());
    }

    [Theory]
    [InlineData("downloading")]
    [InlineData("applying")]
    public async Task Transfers_WaitingBehindAnActiveTransfer_IsBusyWithItsFile(string status)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        await SetClientVersionAsync(app, pc.Id, "0.26.0");
        await SeedTransferAsync(app, pc.Id, status, fileName: "lesson.txt", updatedUtc: app.Time.GetUtcNow() - TimeSpan.FromSeconds(119));
        var id = await SeedTransferAsync(app, pc.Id, "waiting");

        var transfer = await TransferAsync(admin, id);

        Assert.Equal("busy", transfer.GetProperty("waitingReason").GetString());
        Assert.Equal("lesson.txt", transfer.GetProperty("busyWith").GetString());
    }

    // The PC's next heartbeat hands an abandoned transfer over again before this one, so the PC counts as busy with it.
    [Fact]
    public async Task Transfers_WaitingBehindATransferToHandOverAgain_IsBusyWithIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        await SetClientVersionAsync(app, pc.Id, "0.26.0");
        var now = app.Time.GetUtcNow();
        await SeedTransferAsync(app, pc.Id, "downloading", createdUtc: now - TimeSpan.FromMinutes(10), updatedUtc: now - TimeSpan.FromMinutes(2), fileName: "lesson.txt", handOverCount: 2);
        var id = await SeedTransferAsync(app, pc.Id, "waiting");

        var transfer = await TransferAsync(admin, id);

        Assert.Equal("busy", transfer.GetProperty("waitingReason").GetString());
        Assert.Equal("lesson.txt", transfer.GetProperty("busyWith").GetString());
    }

    // An abandoned transfer that won't be handed over again (out of hand-overs, or past its two hours) doesn't hold the PC.
    [Theory]
    [InlineData(3, 10)]
    [InlineData(1, 120)]
    public async Task Transfers_WaitingBehindATransferThatWontBeHandedOverAgain_HasNoReason(int handOverCount, int ageMinutes)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        await SetClientVersionAsync(app, pc.Id, "0.26.0");
        var now = app.Time.GetUtcNow();
        await SeedTransferAsync(app, pc.Id, "downloading", createdUtc: now - TimeSpan.FromMinutes(ageMinutes), updatedUtc: now - TimeSpan.FromMinutes(2), handOverCount: handOverCount);
        var id = await SeedTransferAsync(app, pc.Id, "waiting");

        var transfer = await TransferAsync(admin, id);

        Assert.Equal(System.Text.Json.JsonValueKind.Null, transfer.GetProperty("waitingReason").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, transfer.GetProperty("busyWith").ValueKind);
    }

    [Fact]
    public async Task Transfers_AnotherPcsActiveTransfer_DoesNotMakeThisOneBusy()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-01");
        var other = await AddPcAsync(app, "PC-02");
        await SetClientVersionAsync(app, pc.Id, "0.26.0");
        await SeedTransferAsync(app, other.Id, "downloading");
        var id = await SeedTransferAsync(app, pc.Id, "waiting");

        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await TransferAsync(admin, id)).GetProperty("waitingReason").ValueKind);
    }

    // Only a waiting transfer has a reason, even on an offline PC with an old client.
    [Theory]
    [InlineData("downloading", 0)]
    [InlineData("exists", 0)]
    [InlineData("saved", 0)]
    [InlineData("cancelled", 0)]
    [InlineData("waiting", 60)]
    public async Task Transfers_NotWaiting_HasNoReason(string status, int ageMinutes)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, seenAgo: TimeSpan.FromHours(1));
        var id = await SeedTransferAsync(app, pc.Id, status, createdUtc: app.Time.GetUtcNow() - TimeSpan.FromMinutes(ageMinutes));

        var transfer = await TransferAsync(admin, id);

        Assert.NotEqual("waiting", transfer.GetProperty("status").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, transfer.GetProperty("waitingReason").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, transfer.GetProperty("busyWith").ValueKind);
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

    // Cancelling

    [Theory]
    [InlineData("waiting")]
    [InlineData("downloading")]
    [InlineData("exists")]
    [InlineData("applying")]
    public async Task Cancel_OpenTransfer_BecomesCancelled(string status)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, status, createdUtc: app.Time.GetUtcNow() - TimeSpan.FromMinutes(5));

        var transfer = (await TransfersOfAsync(await CancelAsync(admin, id))).Single();

        Assert.Equal("cancelled", transfer.GetProperty("status").GetString());
        var stored = await StoredTransferAsync(app, id);
        Assert.Equal("cancelled", stored.Status);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.UpdatedUtc.ToUnixTimeMilliseconds());
    }

    [Theory]
    [InlineData("saved")]
    [InlineData("skipped")]
    [InlineData("failed")]
    [InlineData("expired")]
    [InlineData("cancelled")]
    public async Task Cancel_FinishedTransfer_IsReturnedUnchanged(string status)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var created = app.Time.GetUtcNow() - TimeSpan.FromMinutes(5);
        var id = await SeedTransferAsync(app, pc.Id, status, createdUtc: created);

        var transfer = (await TransfersOfAsync(await CancelAsync(admin, id))).Single();

        Assert.Equal(status, transfer.GetProperty("status").GetString());
        var stored = await StoredTransferAsync(app, id);
        Assert.Equal(status, stored.Status);
        Assert.Equal(created.ToUnixTimeMilliseconds(), stored.UpdatedUtc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Cancel_ReturnsEveryListedTransfer_AndSkipsUnknownIds()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var waiting = await SeedTransferAsync(app, pc.Id, "waiting");
        var saved = await SeedTransferAsync(app, pc.Id, "saved");
        var untouched = await SeedTransferAsync(app, pc.Id, "waiting");

        var transfers = await TransfersOfAsync(await CancelAsync(admin, waiting, saved, 99999));

        Assert.Equal([waiting, saved], transfers.Select(t => t.GetProperty("id").GetInt64()));
        Assert.Equal(["cancelled", "saved"], transfers.Select(t => t.GetProperty("status").GetString()));
        Assert.Equal("waiting", (await StoredTransferAsync(app, untouched)).Status);
    }

    // The PC that already started it can no longer download it or report on it.
    [Fact]
    public async Task Cancel_InProgressTransfer_ThePcCanNoLongerDownloadOrReport()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var fileId = await UploadIdAsync(admin, Bytes("cancel me"));
        await SendTransfersAsync(admin, fileId, pc.Id);
        var id = Assert.Single(await HandedTransfersAsync(client, pc)).GetProperty("id").GetInt64();

        await CancelAsync(admin, id);

        Assert.Equal(HttpStatusCode.NotFound, (await PcDownloadAsync(client, id, pc.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ReportAsync(client, id, pc.Token, new { status = "saved" })).StatusCode);
        Assert.Equal("cancelled", (await StoredTransferAsync(app, id)).Status);
        Assert.Empty(await HandedTransfersAsync(client, pc));
    }

    [Theory]
    [InlineData("""{"transferIds":[]}""")]
    [InlineData("""{}""")]
    [InlineData("""{"transferIds":"1"}""")]
    [InlineData("""{"transferIds":[0]}""")]
    [InlineData("""{"transferIds":[5,-1]}""")]
    [InlineData("null")]
    public async Task Cancel_InvalidBody_Returns400(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await admin.PostAsync(CancelRoute, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_MoreThan200Transfers_Returns400AndChangesNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, "waiting");

        var response = await CancelAsync(admin, [id, .. Enumerable.Range(100000, 200).Select(i => (long)i)]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("waiting", (await StoredTransferAsync(app, id)).Status);
    }

    [Fact]
    public async Task Cancel_FromThePcPort_Returns404()
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, "waiting");

        var response = await CancelAsync(pcClient, id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("waiting", (await StoredTransferAsync(app, id)).Status);
    }

    private static async Task<System.Text.Json.JsonElement> TransferAsync(HttpClient admin, long id) =>
        (await TransfersOfAsync(await admin.GetAsync($"{TransfersRoute}?ids={id}"))).Single();

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
