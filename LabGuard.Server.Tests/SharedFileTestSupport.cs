using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using LabGuard.Server.Data;
using LabGuard.Server.SharedFiles;
using Microsoft.EntityFrameworkCore;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// Stands in for the disk, so a test chooses how much free space the server sees. Each call asks AvailableFor how many bytes
// have been asked about so far, so a test can make the disk fill up partway through an upload.
internal sealed class FakeDiskSpace : IDiskSpace
{
    private int _calls;

    public Func<int, long> AvailableFor { get; set; } = _ => long.MaxValue / 2;

    public int Calls => _calls;

    public long AvailableBytes(string folder) => AvailableFor(Interlocked.Increment(ref _calls));
}

// A body of the given length, made as it is read, with no length the client can see, so it is sent chunked.
internal sealed class GeneratedBodyStream(long length) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var size = (int)Math.Min(count, length - _position);
        Array.Clear(buffer, offset, size);
        _position += size;
        return size;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// Helpers shared by the send-file tests: the administrator's uploads and sends, and the PC's heartbeat, download and reports.
internal static class SharedFileTestSupport
{
    internal const string UploadRoute = "/api/shared-files";
    internal const string SendRoute = "/api/pcs/files";
    internal const string TransfersRoute = "/api/pcs/files/transfers";
    internal const string DecisionRoute = "/api/pcs/files/transfers/decision";
    internal const string CancelRoute = "/api/pcs/files/transfers/cancel";
    internal const string InvalidFileNameMessage = "Choose a file with a valid Windows file name.";
    internal const string NoDiskSpaceMessage = "The server does not have enough free disk space for this file.";

    internal static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    internal static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // The upload as the dashboard sends it: the raw bytes, with the name URL-encoded in a header. A null name sends no header.
    internal static Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] bytes, string? encodedName = "notes.pdf", string contentType = "application/octet-stream") =>
        UploadContentAsync(client, new ByteArrayContent(bytes), encodedName, contentType);

    internal static async Task<HttpResponseMessage> UploadContentAsync(HttpClient client, HttpContent content, string? encodedName, string contentType = "application/octet-stream")
    {
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var request = new HttpRequestMessage(HttpMethod.Post, UploadRoute) { Content = content };
        if (encodedName is not null)
        {
            request.Headers.TryAddWithoutValidation("X-File-Name", encodedName);
        }

        return await client.SendAsync(request);
    }

    // The id of a file the administrator has just uploaded.
    internal static async Task<int> UploadIdAsync(HttpClient admin, byte[] bytes, string name = "notes.pdf")
    {
        var response = await UploadAsync(admin, bytes, Uri.EscapeDataString(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonOf(response)).GetProperty("id").GetInt32();
    }

    internal static Task<HttpResponseMessage> SendAsync(HttpClient admin, object body) => admin.PostAsJsonAsync(SendRoute, body);

    // Sends the file to the PCs with the default folder and subfolder, and returns the transfers created.
    internal static async Task<JsonElement[]> SendTransfersAsync(HttpClient admin, int sharedFileId, params int[] pcIds)
    {
        var response = await SendAsync(admin, new { ids = pcIds, sharedFileId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("transfers").EnumerateArray().ToArray();
    }

    internal static async Task<JsonElement[]> TransfersOfAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("transfers").EnumerateArray().ToArray();
    }

    internal static Task<HttpResponseMessage> CancelAsync(HttpClient admin, params long[] transferIds) =>
        admin.PostAsJsonAsync(CancelRoute, new { transferIds });

    internal static Task<HttpResponseMessage> DecideAsync(HttpClient admin, string decision, params long[] transferIds) =>
        admin.PostAsJsonAsync(DecisionRoute, new { transferIds, decision });

    // The first client version that is handed sent files.
    internal const string FileClientVersion = "0.26.0";

    // A heartbeat from a client reporting the given version. Null reports none, as a client older than 0.20.0 does.
    internal static Task<HttpResponseMessage> VersionedHeartbeatAsync(HttpClient client, TestPc pc, string? clientVersion = FileClientVersion) =>
        PostAsync(client, "/api/client/heartbeat", pc.Token, JsonContent.Create(new { clientVersion }));

    // The file transfers a heartbeat hands over, or none when the field is left out.
    internal static async Task<JsonElement[]> HandedTransfersAsync(HttpClient client, TestPc pc, string? clientVersion = FileClientVersion)
    {
        var body = await JsonOf(await VersionedHeartbeatAsync(client, pc, clientVersion));
        return body.TryGetProperty("fileTransfers", out var transfers) ? transfers.EnumerateArray().ToArray() : [];
    }

    internal static async Task<HttpResponseMessage> PcDownloadAsync(HttpClient pc, long transferId, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/client/files/{transferId}");
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        return await pc.SendAsync(request);
    }

    internal static Task<HttpResponseMessage> ReportAsync(HttpClient pc, long transferId, string? token, object body) =>
        PostAsync(pc, $"/api/client/files/{transferId}/status", token, JsonContent.Create(body));

    // A transfer stored straight in the database, for tests that need a status or age a real flow can't reach quickly.
    internal static Task<long> SeedTransferAsync(
        TestApp app,
        int pcId,
        string status,
        string? decision = null,
        DateTimeOffset? createdUtc = null,
        DateTimeOffset? updatedUtc = null,
        int? sharedFileId = null,
        string fileName = "notes.pdf",
        string? sha256 = null,
        int handOverCount = 0) =>
        app.WithDbAsync(async db =>
        {
            var now = app.Time.GetUtcNow();
            var transfer = new PcFileTransfer
            {
                PcId = pcId,
                PcName = "PC-01",
                SharedFileId = sharedFileId,
                FileName = fileName,
                SizeBytes = 3,
                Sha256 = sha256 ?? new string('a', 64),
                Folder = "desktop",
                Subfolder = "Lykantra Share",
                Status = status,
                Decision = decision,
                CreatedUtc = createdUtc ?? now,
                UpdatedUtc = updatedUtc ?? createdUtc ?? now,
                HandOverCount = handOverCount,
            };
            db.PcFileTransfers.Add(transfer);
            await db.SaveChangesAsync();
            return transfer.Id;
        });

    // A shared file row stored straight in the database, with its bytes written where the server keeps them.
    internal static Task<int> SeedSharedFileAsync(TestApp app, byte[] bytes, DateTimeOffset createdUtc) =>
        app.WithDbAsync(async db =>
        {
            var sha256 = Sha256Of(bytes);
            Directory.CreateDirectory(app.SharedFilesFolder);
            await File.WriteAllBytesAsync(Path.Combine(app.SharedFilesFolder, sha256), bytes);
            var file = new SharedFile { Name = "notes.pdf", SizeBytes = bytes.Length, Sha256 = sha256, CreatedUtc = createdUtc };
            db.SharedFiles.Add(file);
            await db.SaveChangesAsync();
            return file.Id;
        });

    // Stores the client version the PC last reported, as its heartbeat would. Null means it reported none.
    internal static Task SetClientVersionAsync(TestApp app, int pcId, string? clientVersion) =>
        app.WithDbAsync(db => db.Pcs.Where(p => p.Id == pcId).ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ClientVersion, clientVersion)));

    internal static Task<PcFileTransfer> StoredTransferAsync(TestApp app, long id) =>
        app.WithDbAsync(db => db.PcFileTransfers.AsNoTracking().SingleAsync(t => t.Id == id));

    internal static Task<bool> TransferExistsAsync(TestApp app, long id) =>
        app.WithDbAsync(db => db.PcFileTransfers.AnyAsync(t => t.Id == id));

    // The files the server keeps for sharing. Empty when the folder was never created.
    internal static string[] StoredFilesIn(TestApp app) =>
        Directory.Exists(app.SharedFilesFolder)
            ? Directory.GetFiles(app.SharedFilesFolder).Select(path => Path.GetFileName(path)!).ToArray()
            : [];
}
