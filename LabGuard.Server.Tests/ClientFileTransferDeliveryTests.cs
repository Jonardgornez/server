using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;
using static LabGuard.Server.Tests.SharedFileTestSupport;

namespace LabGuard.Server.Tests;

// The PC gets sent files on its heartbeat, downloads only its own, and reports how each one went.
public class ClientFileTransferDeliveryTests
{
    // Heartbeat hand-over

    [Fact]
    public async Task Heartbeat_WithoutTransfers_HasNoFileTransfersField()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);

        var body = await HeartbeatBodyAsync(client, pc);

        Assert.False(body.TryGetProperty("fileTransfers", out _));
    }

    [Fact]
    public async Task Heartbeat_HandsOverAWaitingTransfer_AndMarksItDownloading()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var bytes = Bytes("lesson");
        var fileId = await UploadIdAsync(admin, bytes, "lesson.txt");
        var sent = await TransfersOfAsync(await SendAsync(admin, new { ids = new[] { pc.Id }, sharedFileId = fileId, folder = "documents", subfolder = "a/b" }));
        var transferId = sent.Single().GetProperty("id").GetInt64();

        var handed = Assert.Single(await HandedTransfersAsync(client, pc));

        Assert.Equal(transferId, handed.GetProperty("id").GetInt64());
        Assert.Equal("lesson.txt", handed.GetProperty("fileName").GetString());
        Assert.Equal(bytes.Length, handed.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(Sha256Of(bytes), handed.GetProperty("sha256").GetString());
        Assert.Equal("documents", handed.GetProperty("folder").GetString());
        Assert.Equal("a\\b", handed.GetProperty("subfolder").GetString());
        Assert.Equal(JsonValueKind.Null, handed.GetProperty("decision").ValueKind);
        Assert.Equal("downloading", (await StoredTransferAsync(app, transferId)).Status);
    }

    [Fact]
    public async Task Heartbeat_HandsOverEachTransferOnce()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await SeedTransferAsync(app, pc.Id, "waiting");

        var first = await HandedTransfersAsync(client, pc);
        var second = await HandedTransfersAsync(client, pc);

        Assert.Single(first);
        Assert.Empty(second);
    }

    // One at a time: the oldest first, and the next only once the PC has finished the one it was working on.
    [Fact]
    public async Task Heartbeat_HandsOverOneAtATime_OldestFirst()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();
        var older = await SeedTransferAsync(app, pc.Id, "waiting", createdUtc: now - TimeSpan.FromMinutes(2));
        var newer = await SeedTransferAsync(app, pc.Id, "waiting", createdUtc: now - TimeSpan.FromMinutes(1));

        var first = await HandedTransfersAsync(client, pc);
        var whileBusy = await HandedTransfersAsync(client, pc);
        await ReportAsync(client, older, pc.Token, new { status = "saved" });
        var afterwards = await HandedTransfersAsync(client, pc);

        Assert.Equal([older], first.Select(t => t.GetProperty("id").GetInt64()));
        Assert.Empty(whileBusy);
        Assert.Equal([newer], afterwards.Select(t => t.GetProperty("id").GetInt64()));
    }

    [Theory]
    [InlineData("downloading")]
    [InlineData("applying")]
    public async Task Heartbeat_HandsOverNothing_WhileThePcIsWorkingOnATransfer(string busyStatus)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await SeedTransferAsync(app, pc.Id, busyStatus);
        var waiting = await SeedTransferAsync(app, pc.Id, "waiting");
        var decided = await SeedTransferAsync(app, pc.Id, "exists", decision: "overwrite");

        Assert.Empty(await HandedTransfersAsync(client, pc));
        Assert.Equal("waiting", (await StoredTransferAsync(app, waiting)).Status);
        Assert.Equal("exists", (await StoredTransferAsync(app, decided)).Status);
    }

    [Fact]
    public async Task Heartbeat_AnotherPcsBusyTransfer_DoesNotHoldThisPcBack()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app, "PC-01");
        var other = await AddPcAsync(app, "PC-02");
        await SeedTransferAsync(app, other.Id, "downloading");
        await SeedTransferAsync(app, pc.Id, "waiting");

        Assert.Single(await HandedTransfersAsync(client, pc));
    }

    [Fact]
    public async Task Heartbeat_ConcurrentHeartbeats_HandOverOnlyOneTransfer()
    {
        using var app = new TestApp();
        var pc = await AddPcAsync(app);
        for (var i = 0; i < 4; i++)
        {
            await SeedTransferAsync(app, pc.Id, "waiting", createdUtc: app.Time.GetUtcNow() - TimeSpan.FromSeconds(10 - i));
        }

        var clients = Enumerable.Range(0, 4).Select(i => app.CreatePcClient($"192.168.1.{60 + i}")).ToArray();
        var handed = await Task.WhenAll(clients.Select(client => HandedTransfersAsync(client, pc)));

        Assert.Equal(1, handed.Sum(transfers => transfers.Length));
        Assert.Equal(1, await app.WithDbAsync(db => db.PcFileTransfers.CountAsync(t => t.Status == "downloading")));
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task Heartbeat_DoesNotHandOverAWaitingTransferOlderThanAnHour()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, "waiting", createdUtc: app.Time.GetUtcNow() - TimeSpan.FromHours(1));

        Assert.Empty(await HandedTransfersAsync(client, pc));
        Assert.Equal("waiting", (await StoredTransferAsync(app, id)).Status);
    }

    [Fact]
    public async Task Heartbeat_HandsOverAnExistsTransferOnlyOnceDecided_AndMarksItApplying()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, "exists");

        var beforeDecision = await HandedTransfersAsync(client, pc);
        await DecideAsync(admin, "keepBoth", id);
        var afterDecision = await HandedTransfersAsync(client, pc);
        var again = await HandedTransfersAsync(client, pc);

        Assert.Empty(beforeDecision);
        var handed = Assert.Single(afterDecision);
        Assert.Equal("keepBoth", handed.GetProperty("decision").GetString());
        Assert.Equal("applying", (await StoredTransferAsync(app, id)).Status);
        Assert.Empty(again);
    }

    [Theory]
    [InlineData("downloading")]
    [InlineData("applying")]
    [InlineData("saved")]
    [InlineData("skipped")]
    [InlineData("failed")]
    [InlineData("expired")]
    public async Task Heartbeat_DoesNotHandOverOtherStatuses(string status)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await SeedTransferAsync(app, pc.Id, status, decision: "overwrite");

        Assert.Empty(await HandedTransfersAsync(client, pc));
    }

    [Fact]
    public async Task Heartbeat_HandsOverOnlyThisPcsTransfers()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app, "PC-01");
        var other = await AddPcAsync(app, "PC-02");
        var otherTransfer = await SeedTransferAsync(app, other.Id, "waiting");

        Assert.Empty(await HandedTransfersAsync(client, pc));
        Assert.Equal("waiting", (await StoredTransferAsync(app, otherTransfer)).Status);
    }

    // Download

    // A real file, sent to the PC and handed over, so it is downloading.
    private static async Task<long> DownloadingTransferAsync(TestApp app, HttpClient admin, HttpClient client, TestPc pc, byte[] bytes)
    {
        var fileId = await UploadIdAsync(admin, bytes);
        await SendTransfersAsync(admin, fileId, pc.Id);
        return Assert.Single(await HandedTransfersAsync(client, pc)).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task Download_OwnDownloadingTransfer_StreamsTheBytes()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var bytes = Bytes("the file bytes");
        var id = await DownloadingTransferAsync(app, admin, client, pc, bytes);

        var response = await PcDownloadAsync(client, id, pc.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Download_AnotherPcsTransfer_LooksMissing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app, "PC-01");
        var other = await AddPcAsync(app, "PC-02");
        var id = await DownloadingTransferAsync(app, admin, client, pc, Bytes("private"));

        var response = await PcDownloadAsync(client, id, other.Token);
        var missing = await PcDownloadAsync(client, 99999, other.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Download_WithoutToken_Returns401()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await PcDownloadAsync(client, 1, null)).StatusCode);
    }

    [Theory]
    [InlineData("waiting")]
    [InlineData("exists")]
    [InlineData("saved")]
    [InlineData("skipped")]
    [InlineData("failed")]
    [InlineData("expired")]
    public async Task Download_TransferNotInProgress_Returns404(string status)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var bytes = Bytes("abc");
        var fileId = await SeedSharedFileAsync(app, bytes, app.Time.GetUtcNow());
        var id = await SeedTransferAsync(app, pc.Id, status, sharedFileId: fileId, sha256: Sha256Of(bytes));

        Assert.Equal(HttpStatusCode.NotFound, (await PcDownloadAsync(client, id, pc.Token)).StatusCode);
    }

    [Theory]
    [InlineData("downloading")]
    [InlineData("applying")]
    public async Task Download_RecordsTheRequestAsActivity(string status)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var bytes = Bytes("active");
        var fileId = await SeedSharedFileAsync(app, bytes, app.Time.GetUtcNow());
        var handedOverAt = app.Time.GetUtcNow() - TimeSpan.FromMinutes(50);
        var id = await SeedTransferAsync(app, pc.Id, status, createdUtc: handedOverAt, sharedFileId: fileId, sha256: Sha256Of(bytes));

        var response = await PcDownloadAsync(client, id, pc.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), (await StoredTransferAsync(app, id)).UpdatedUtc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Download_AnotherPcsTransfer_DoesNotRecordActivity()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app, "PC-01");
        var other = await AddPcAsync(app, "PC-02");
        var before = app.Time.GetUtcNow() - TimeSpan.FromMinutes(50);
        var id = await SeedTransferAsync(app, pc.Id, "downloading", createdUtc: before);

        await PcDownloadAsync(client, id, other.Token);

        Assert.Equal(before.ToUnixTimeMilliseconds(), (await StoredTransferAsync(app, id)).UpdatedUtc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Download_MoreThanSixAMinuteFromOneAddress_Returns429()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        using var otherAddress = app.CreatePcClient("192.168.1.51");
        var pc = await AddPcAsync(app);
        var id = await DownloadingTransferAsync(app, admin, client, pc, Bytes("again"));

        for (var download = 1; download <= 6; download++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PcDownloadAsync(client, id, pc.Token)).StatusCode);
        }

        var refused = await PcDownloadAsync(client, id, pc.Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var retryAfter = refused.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.InRange(retryAfter.Value.TotalSeconds, 1, 60);
        // The reports have their own limit, and another address its own downloads.
        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(client, id, pc.Token, new { status = "saved" })).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PcDownloadAsync(otherAddress, id, pc.Token)).StatusCode);
    }

    [Fact]
    public async Task Download_FileGoneFromDisk_Returns404()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var bytes = Bytes("gone");
        var id = await DownloadingTransferAsync(app, admin, client, pc, bytes);
        File.Delete(Path.Combine(app.SharedFilesFolder, Sha256Of(bytes)));

        Assert.Equal(HttpStatusCode.NotFound, (await PcDownloadAsync(client, id, pc.Token)).StatusCode);
    }

    // Reports

    [Theory]
    [InlineData("downloading", "saved")]
    [InlineData("downloading", "exists")]
    [InlineData("downloading", "failed")]
    [InlineData("applying", "saved")]
    [InlineData("applying", "skipped")]
    [InlineData("applying", "failed")]
    public async Task Report_AllowedTransition_IsStored(string from, string to)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, from);

        var response = await ReportAsync(client, id, pc.Token, new { status = to });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(to, (await StoredTransferAsync(app, id)).Status);
    }

    [Theory]
    [InlineData("waiting", "saved")]
    [InlineData("exists", "saved")]
    [InlineData("saved", "saved")]
    [InlineData("saved", "failed")]
    [InlineData("applying", "exists")]
    [InlineData("downloading", "skipped")]
    [InlineData("failed", "saved")]
    [InlineData("expired", "saved")]
    public async Task Report_DisallowedTransition_Returns409AndChangesNothing(string from, string to)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, from);

        var response = await ReportAsync(client, id, pc.Token, new { status = to });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(from, (await StoredTransferAsync(app, id)).Status);
    }

    [Fact]
    public async Task Report_Saved_StoresTheNameItWasSavedAs()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, "applying");

        await ReportAsync(client, id, pc.Token, new { status = "saved", savedAs = "notes (2).pdf" });

        Assert.Equal("notes (2).pdf", (await StoredTransferAsync(app, id)).SavedAs);
    }

    [Fact]
    public async Task Report_SavedWithoutAName_UsesTheSentName()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, "downloading", fileName: "lesson.txt");

        await ReportAsync(client, id, pc.Token, new { status = "saved" });

        Assert.Equal("lesson.txt", (await StoredTransferAsync(app, id)).SavedAs);
    }

    [Theory]
    [InlineData("..\\evil.exe")]
    [InlineData("CON")]
    [InlineData("REPORT~1.PDF")]
    [InlineData("desktop.ini")]
    [InlineData("")]
    public async Task Report_SavedWithAnInvalidName_Returns400(string savedAs)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, "downloading");

        var response = await ReportAsync(client, id, pc.Token, new { status = "saved", savedAs });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("downloading", (await StoredTransferAsync(app, id)).Status);
    }

    [Fact]
    public async Task Report_Failed_StoresTheCleanedError()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, "downloading");

        await ReportAsync(client, id, pc.Token, new { status = "failed", error = " Not enough free space\n" + new string('x', 300) });

        var error = (await StoredTransferAsync(app, id)).Error!;
        Assert.StartsWith("Not enough free space", error);
        Assert.Equal(200, error.Length);
    }

    [Theory]
    [InlineData("waiting")]
    [InlineData("expired")]
    [InlineData("SAVED")]
    [InlineData("")]
    public async Task Report_StatusAPcCantReport_Returns400(string status)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, "downloading");

        var response = await ReportAsync(client, id, pc.Token, new { status });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Report_AnotherPcsTransfer_Returns404AndChangesNothing()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app, "PC-01");
        var other = await AddPcAsync(app, "PC-02");
        var id = await SeedTransferAsync(app, pc.Id, "downloading");

        var response = await ReportAsync(client, id, other.Token, new { status = "failed" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("downloading", (await StoredTransferAsync(app, id)).Status);
    }

    [Fact]
    public async Task Report_WithoutToken_Returns401()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await ReportAsync(client, 1, null, new { status = "saved" })).StatusCode);
    }
}
