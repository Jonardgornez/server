using System.Net;
using System.Text.Json;
using LabGuard.Server.SharedFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    // The client restarted mid-transfer (an update or a reboot), so it will never finish it. The next heartbeat hands it over
    // again, and once it is done the waiting transfer behind it follows, instead of waiting until it expires.
    [Theory]
    [InlineData("downloading", null)]
    [InlineData("applying", "overwrite")]
    public async Task Heartbeat_AbandonedTransfer_IsHandedOverAgain_AndTheNextOneFollows(string status, string? decision)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();
        var abandoned = await SeedTransferAsync(app, pc.Id, status, decision: decision, createdUtc: now - TimeSpan.FromMinutes(10), updatedUtc: now - TimeSpan.FromMinutes(3));
        var waiting = await SeedTransferAsync(app, pc.Id, "waiting", createdUtc: now - TimeSpan.FromMinutes(5));

        var again = Assert.Single(await HandedTransfersAsync(client, pc));
        await ReportAsync(client, abandoned, pc.Token, new { status = "saved" });
        var next = Assert.Single(await HandedTransfersAsync(client, pc));

        Assert.Equal(abandoned, again.GetProperty("id").GetInt64());
        Assert.Equal(decision, again.GetProperty("decision").GetString());
        Assert.Equal(waiting, next.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Heartbeat_HandingOverAgain_KeepsTheStatus_AndRecordsActivity()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();
        var id = await SeedTransferAsync(app, pc.Id, "downloading", createdUtc: now - TimeSpan.FromMinutes(10), updatedUtc: now - TimeSpan.FromMinutes(2));

        Assert.Single(await HandedTransfersAsync(client, pc));

        var stored = await StoredTransferAsync(app, id);
        Assert.Equal("downloading", stored.Status);
        Assert.Equal(now.ToUnixTimeMilliseconds(), stored.UpdatedUtc.ToUnixTimeMilliseconds());
        Assert.Equal(1, stored.HandOverCount);
        // Handed over again, it counts as busy again, so the next heartbeat hands nothing.
        Assert.Empty(await HandedTransfersAsync(client, pc));
    }

    // Handed over again, the PC downloads from the start, so the progress recorded before starts again too.
    [Fact]
    public async Task Heartbeat_HandingOverAgain_ForgetsTheProgress()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();
        var id = await SeedTransferAsync(app, pc.Id, "downloading", createdUtc: now - TimeSpan.FromMinutes(10), updatedUtc: now - TimeSpan.FromMinutes(3));
        var progress = ProgressOf(app);
        progress.Record(id, 2);

        Assert.Single(await HandedTransfersAsync(client, pc));

        Assert.Null(progress.BytesSent(id));
    }

    // Activity within the last two minutes means the PC is still at work, so the next transfer still waits.
    [Theory]
    [InlineData("downloading")]
    [InlineData("applying")]
    public async Task Heartbeat_TransferActiveUnderTwoMinutesAgo_StillBlocks(string status)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();
        var lastActivity = now - TimeSpan.FromSeconds(119);
        var active = await SeedTransferAsync(app, pc.Id, status, createdUtc: now - TimeSpan.FromMinutes(10), updatedUtc: lastActivity);
        var waiting = await SeedTransferAsync(app, pc.Id, "waiting");

        Assert.Empty(await HandedTransfersAsync(client, pc));
        Assert.Equal(lastActivity.ToUnixTimeMilliseconds(), (await StoredTransferAsync(app, active)).UpdatedUtc.ToUnixTimeMilliseconds());
        Assert.Equal("waiting", (await StoredTransferAsync(app, waiting)).Status);
    }

    // Past its retries an abandoned transfer is left for the stuck cleanup, and it no longer holds the PC back.
    [Fact]
    public async Task Heartbeat_AbandonedTransferPastItsRetries_IsNotHandedOver_AndDoesNotBlock()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();
        var lastActivity = now - TimeSpan.FromMinutes(10);
        var abandoned = await SeedTransferAsync(app, pc.Id, "downloading", createdUtc: now - TimeSpan.FromHours(2), updatedUtc: lastActivity);
        var waiting = await SeedTransferAsync(app, pc.Id, "waiting");

        var handed = Assert.Single(await HandedTransfersAsync(client, pc));

        Assert.Equal(waiting, handed.GetProperty("id").GetInt64());
        Assert.Equal(lastActivity.ToUnixTimeMilliseconds(), (await StoredTransferAsync(app, abandoned)).UpdatedUtc.ToUnixTimeMilliseconds());
    }

    // A transfer abandoned again after its third hand-over fails, and the next waiting transfer goes out on that heartbeat.
    // Seeded as a row from before hand-overs were counted, so it gets three hand-overs again.
    [Fact]
    public async Task Heartbeat_AfterThreeHandOversAgain_FailsTheAbandonedTransfer_AndHandsOverTheNext()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var abandoned = await SeedTransferAsync(app, pc.Id, "downloading");
        var waiting = await SeedTransferAsync(app, pc.Id, "waiting");

        for (var handOver = 1; handOver <= 3; handOver++)
        {
            app.Time.Advance(TimeSpan.FromMinutes(3));
            var again = Assert.Single(await HandedTransfersAsync(client, pc));
            Assert.Equal(abandoned, again.GetProperty("id").GetInt64());
            Assert.Equal(handOver, (await StoredTransferAsync(app, abandoned)).HandOverCount);
        }

        app.Time.Advance(TimeSpan.FromMinutes(3));
        var next = Assert.Single(await HandedTransfersAsync(client, pc));

        Assert.Equal(waiting, next.GetProperty("id").GetInt64());
        var failed = await StoredTransferAsync(app, abandoned);
        Assert.Equal("failed", failed.Status);
        Assert.Equal("PC stopped responding", failed.Error);
        Assert.Equal("downloading", (await StoredTransferAsync(app, waiting)).Status);
    }

    // The first hand-over counts too: a sent file goes out at most three times in all.
    [Fact]
    public async Task Heartbeat_CountsEveryHandOver_AndStopsAfterThree()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, "waiting");

        var handOvers = 0;
        for (var step = 0; step < 5; step++)
        {
            handOvers += (await HandedTransfersAsync(client, pc)).Length;
            app.Time.Advance(TimeSpan.FromMinutes(3));
        }

        Assert.Equal(3, handOvers);
        var stored = await StoredTransferAsync(app, id);
        Assert.Equal(3, stored.HandOverCount);
        Assert.Equal("failed", stored.Status);
    }

    // Activity after the last hand-over means the PC is working on it, so it isn't failed.
    [Fact]
    public async Task Heartbeat_LastHandOverStillActive_IsNotFailed()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();
        var id = await SeedTransferAsync(app, pc.Id, "downloading", updatedUtc: now - TimeSpan.FromSeconds(119), handOverCount: 3);

        Assert.Empty(await HandedTransfersAsync(client, pc));
        Assert.Equal("downloading", (await StoredTransferAsync(app, id)).Status);
    }

    [Fact]
    public async Task Heartbeat_ConcurrentHeartbeats_HandOverAnAbandonedTransferOnlyOnce()
    {
        using var app = new TestApp();
        var pc = await AddPcAsync(app);
        await SetClientVersionAsync(app, pc.Id, FileClientVersion);
        var now = app.Time.GetUtcNow();
        var abandoned = await SeedTransferAsync(app, pc.Id, "downloading", createdUtc: now - TimeSpan.FromMinutes(20), updatedUtc: now - TimeSpan.FromMinutes(5));
        var waiting = await SeedTransferAsync(app, pc.Id, "waiting", createdUtc: now - TimeSpan.FromMinutes(10));

        var clients = Enumerable.Range(0, 4).Select(i => app.CreatePcClient($"192.168.1.{70 + i}")).ToArray();
        var handed = await Task.WhenAll(clients.Select(client => HandedTransfersAsync(client, pc)));

        Assert.Equal([abandoned], handed.SelectMany(transfers => transfers).Select(t => t.GetProperty("id").GetInt64()));
        Assert.Equal(1, (await StoredTransferAsync(app, abandoned)).HandOverCount);
        Assert.Equal("waiting", (await StoredTransferAsync(app, waiting)).Status);
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    // Version gate: only a client that receives sent files is handed them.
    [Theory]
    [InlineData(null, false)]
    [InlineData("garbage", false)]
    [InlineData("0.25.9", false)]
    [InlineData("0.26.0", true)]
    [InlineData("0.27.0", true)]
    public async Task Heartbeat_HandsOverOnlyToAClientAtLeast0_26_0(string? clientVersion, bool handsOver)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, "waiting");

        var handed = await HandedTransfersAsync(client, pc, clientVersion);

        Assert.Equal(handsOver ? 1 : 0, handed.Length);
        Assert.Equal(handsOver ? "downloading" : "waiting", (await StoredTransferAsync(app, id)).Status);
    }

    [Theory]
    [InlineData("downloading", "0.25.0")]
    [InlineData("applying", "0.25.0")]
    [InlineData("downloading", "0.9.9")]
    public async Task Heartbeat_FromAClientTooOld_FailsTransfersItWasHanded(string status, string clientVersion)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var inProgress = await SeedTransferAsync(app, pc.Id, status);
        var waiting = await SeedTransferAsync(app, pc.Id, "waiting");
        var other = await AddPcAsync(app, "PC-02");
        var otherInProgress = await SeedTransferAsync(app, other.Id, status);

        Assert.Empty(await HandedTransfersAsync(client, pc, clientVersion));

        var failed = await StoredTransferAsync(app, inProgress);
        Assert.Equal("failed", failed.Status);
        Assert.Equal("This PC needs client 0.26.0 or newer.", failed.Error);
        Assert.Equal("waiting", (await StoredTransferAsync(app, waiting)).Status);
        Assert.Equal(status, (await StoredTransferAsync(app, otherInProgress)).Status);
    }

    // Without a readable version nothing says the client is too old, so nothing is failed either.
    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    public async Task Heartbeat_WithoutAReadableVersion_HandsOverNothing_AndFailsNothing(string? clientVersion)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();
        var abandoned = await SeedTransferAsync(app, pc.Id, "downloading", updatedUtc: now - TimeSpan.FromMinutes(5), handOverCount: 3);
        var waiting = await SeedTransferAsync(app, pc.Id, "waiting");

        Assert.Empty(await HandedTransfersAsync(client, pc, clientVersion));

        Assert.Equal("downloading", (await StoredTransferAsync(app, abandoned)).Status);
        Assert.Equal("waiting", (await StoredTransferAsync(app, waiting)).Status);
    }

    // A heartbeat with no body, or one that can't be read, skips the hand-over and leaves the stored version alone.
    [Theory]
    [InlineData(null)]
    [InlineData("{not json")]
    public async Task Heartbeat_WithAnUnreadableBody_SkipsTheHandOver_AndKeepsTheVersion(string? rawBody)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await SetClientVersionAsync(app, pc.Id, "0.25.0");
        var inProgress = await SeedTransferAsync(app, pc.Id, "downloading");
        var waiting = await SeedTransferAsync(app, pc.Id, "waiting");

        var content = rawBody is null ? null : new StringContent(rawBody, System.Text.Encoding.UTF8, "application/json");
        var response = await PostAsync(client, "/api/client/heartbeat", pc.Token, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await JsonOf(response)).TryGetProperty("fileTransfers", out _));
        Assert.Equal("downloading", (await StoredTransferAsync(app, inProgress)).Status);
        Assert.Equal("waiting", (await StoredTransferAsync(app, waiting)).Status);
        Assert.Equal("0.25.0", (await app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync(p => p.Id == pc.Id))).ClientVersion);
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
    [InlineData("cancelled")]
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

    private static FileTransferProgress ProgressOf(TestApp app) => app.Services.GetRequiredService<FileTransferProgress>();

    [Fact]
    public async Task Download_RecordsHowFarThePcHasGot()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var bytes = Bytes("the file bytes");
        var id = await DownloadingTransferAsync(app, admin, client, pc, bytes);

        await (await PcDownloadAsync(client, id, pc.Token)).Content.ReadAsByteArrayAsync();

        Assert.Equal(bytes.Length, ProgressOf(app).BytesSent(id));
    }

    // A resumed download asks for a range, so the position counts from where the range starts.
    [Fact]
    public async Task Download_ARange_RecordsTheAbsolutePosition()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await DownloadingTransferAsync(app, admin, client, pc, Bytes("the file bytes"));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/client/files/{id}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {pc.Token}");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(4, 7);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(Bytes("file"), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(8, ProgressOf(app).BytesSent(id));
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
    [InlineData("cancelled")]
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
    [InlineData("cancelled", "saved")]
    [InlineData("cancelled", "exists")]
    [InlineData("cancelled", "skipped")]
    [InlineData("cancelled", "failed")]
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

    [Theory]
    [InlineData("downloading", "saved")]
    [InlineData("downloading", "exists")]
    [InlineData("downloading", "failed")]
    [InlineData("applying", "skipped")]
    public async Task Report_Stored_ForgetsTheProgress(string from, string to)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, from);
        var progress = ProgressOf(app);
        progress.Record(id, 2);

        await ReportAsync(client, id, pc.Token, new { status = to });

        Assert.Null(progress.BytesSent(id));
    }

    [Fact]
    public async Task Report_Refused_KeepsTheProgress()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var id = await SeedTransferAsync(app, pc.Id, "downloading");
        var progress = ProgressOf(app);
        progress.Record(id, 2);

        var response = await ReportAsync(client, id, pc.Token, new { status = "skipped" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(2, progress.BytesSent(id));
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
