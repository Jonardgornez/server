using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabGuard.Server.ClientUpdates;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// Stands in for the version resource reader, so a test chooses the product and version without building a real program.
internal sealed class FakeInstallerInspector : IInstallerInspector
{
    public InstallerInfo? Next { get; set; } = new(ClientUpdateEndpoints.ProductName, "0.20.0.0");

    public InstallerInfo? Read(string path) => Next;
}

// A body of the given length: it starts like a Windows program, then zeros. It is made as it is read, so a 150 MB body costs no memory.
internal sealed class InstallerBodyStream(long length) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var size = (int)Math.Min(count, length - _position);
        for (var i = 0; i < size; i++)
        {
            var at = _position + i;
            buffer[offset + i] = at < 2 ? "MZ"u8[(int)at] : (byte)0;
        }

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

internal static class ClientUpdateTestSupport
{
    public const string ReleaseRoute = "/api/client-updates";
    public const string OfferRoute = "/api/pcs/client-update";
    public const string CancelRoute = "/api/pcs/client-update/cancel";
    public const string HeartbeatRoute = "/api/client/heartbeat";
    public const string WrongInstallerMessage = "Choose the Lykantra Client installer (LykantraSetup.exe).";
    public const string TooLargeMessage = "The installer must be 150 MB or smaller.";
    public const string NoReleaseMessage = "Upload a client installer first.";
    public const long InstallerLimitBytes = 150L * 1024 * 1024;

    // A test installer for a version. Its bytes differ per version, so each release has its own hash.
    public static byte[] Installer(string version)
    {
        var bytes = new byte[64];
        "MZ"u8.CopyTo(bytes);
        Encoding.ASCII.GetBytes(version).CopyTo(bytes, 16);
        return bytes;
    }

    public static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static ByteArrayContent InstallerContent(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    public static Task<HttpResponseMessage> UploadAsync(HttpClient admin, byte[] bytes) =>
        admin.PutAsync(ReleaseRoute, InstallerContent(bytes));

    // Uploads an installer of the given version as the stored release. The fake reader says it is that version.
    public static async Task<JsonElement> StoreReleaseAsync(HttpClient admin, FakeInstallerInspector inspector, string version)
    {
        inspector.Next = new InstallerInfo(ClientUpdateEndpoints.ProductName, $"{version}.0");
        var response = await UploadAsync(admin, Installer(version));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonOf(response);
    }

    public static async Task<JsonElement> ReleaseOfAsync(HttpClient admin)
    {
        var status = await JsonOf(await admin.GetAsync(ReleaseRoute));
        return status.GetProperty("release");
    }

    public static Task<HttpResponseMessage> OfferAsync(HttpClient admin, string version, params int[] pcIds) =>
        admin.PostAsJsonAsync(OfferRoute, new { pcIds, version });

    // Offers the release that is stored now, as the administrator's dashboard does.
    public static async Task<HttpResponseMessage> OfferStoredAsync(HttpClient admin, params int[] pcIds)
    {
        var version = (await ReleaseOfAsync(admin)).GetProperty("version").GetString()!;
        return await OfferAsync(admin, version, pcIds);
    }

    // Offers the stored release with the administrator's "install now" choice, as the update dialog sends it.
    public static async Task<HttpResponseMessage> OfferStoredInstallNowAsync(HttpClient admin, bool installNow, params int[] pcIds)
    {
        var version = (await ReleaseOfAsync(admin)).GetProperty("version").GetString()!;
        return await admin.PostAsJsonAsync(OfferRoute, new { pcIds, version, installNow });
    }

    public static Task<HttpResponseMessage> CancelAsync(HttpClient admin, params int[] pcIds) =>
        admin.PostAsJsonAsync(CancelRoute, new { pcIds });

    public static Task<HttpResponseMessage> DownloadAsync(HttpClient pc, string sha256, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/client/client-update/{sha256}");
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        return pc.SendAsync(request);
    }

    public static Task<HttpResponseMessage> HeartbeatWithAsync(HttpClient pc, TestPc registered, object body) =>
        PostAsync(pc, HeartbeatRoute, registered.Token, JsonContent.Create(body));

    public static Task<HttpResponseMessage> HeartbeatWithRawBodyAsync(HttpClient pc, TestPc registered, string json) =>
        PostAsync(pc, HeartbeatRoute, registered.Token, new StringContent(json, Encoding.UTF8, "application/json"));

    public static Task SetClientVersionAsync(TestApp app, int pcId, string? version) =>
        app.WithDbAsync(db => db.Pcs
            .Where(p => p.Id == pcId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ClientVersion, version)));

    public static Task SetTargetAsync(TestApp app, int pcId, string version) =>
        app.WithDbAsync(db => db.Pcs
            .Where(p => p.Id == pcId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.ClientUpdateTargetVersion, version)));

    public static Task<ClientRelease?> StoredReleaseAsync(TestApp app) =>
        app.WithDbAsync(db => db.ClientReleases.AsNoTracking().FirstOrDefaultAsync());

    public static Task<int> ReleaseRowCountAsync(TestApp app) =>
        app.WithDbAsync(db => db.ClientReleases.CountAsync());

    // The files the server keeps for installers. Empty when the folder was never created.
    public static string[] StoredFilesIn(TestApp app) =>
        Directory.Exists(app.ClientUpdatesFolder)
            ? Directory.GetFiles(app.ClientUpdatesFolder).Select(Path.GetFileName).Select(name => name!).ToArray()
            : [];

    public static async Task<JsonElement?> ClientUpdateOfHeartbeatAsync(HttpResponseMessage response)
    {
        var body = await JsonOf(response);
        return body.TryGetProperty("clientUpdate", out var offer) ? offer : null;
    }

    public static async Task<JsonElement?> ClientUpdateOfListEntryAsync(HttpClient admin, string name)
    {
        var entry = await ListEntryAsync(admin, name);
        return entry.GetProperty("clientUpdate").ValueKind == JsonValueKind.Null ? null : entry.GetProperty("clientUpdate");
    }

    // Stores a release in the test folder with a known version and returns its sha256 hash.
    public static async Task<string> StoredHashAsync(HttpClient admin, FakeInstallerInspector inspector, string version)
    {
        var release = await StoreReleaseAsync(admin, inspector, version);
        return release.GetProperty("sha256").GetString()!;
    }
}
