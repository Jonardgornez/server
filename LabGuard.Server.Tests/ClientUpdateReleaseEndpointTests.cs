using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using LabGuard.Server.ClientUpdates;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Sdk;
using static LabGuard.Server.Tests.ClientUpdateTestSupport;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The administrator's installer: upload, the checks on it, replacement, and deletion.
public class ClientUpdateReleaseEndpointTests
{
    private const string RealInstallerPath = @"C:\Users\sorio\Desktop\client and server\client\installer\output\LykantraSetup.exe";

    [Fact]
    public async Task Get_WithNothingStored_ReturnsNullRelease_AndIsNotCached()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await admin.GetAsync(ReleaseRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(JsonValueKind.Null, (await JsonOf(response)).GetProperty("release").ValueKind);
    }

    [Fact]
    public async Task Upload_StoresTheInstaller_UnderItsHashName_AndReturnsTheRelease()
    {
        var inspector = new FakeInstallerInspector();
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, inspector));
        using var admin = await SignedInAsync(app);
        var bytes = Installer("0.20.0");
        var sha256 = Sha256Of(bytes);

        var response = await UploadAsync(admin, bytes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await JsonOf(response);
        Assert.Equal("0.20.0", body.GetProperty("version").GetString());
        Assert.Equal(sha256, body.GetProperty("sha256").GetString());
        Assert.Equal(bytes.Length, body.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(app.Time.GetUtcNow(), DateTimeOffset.Parse(body.GetProperty("uploadedAtUtc").GetString()!));
        Assert.Equal([$"{sha256}.exe"], StoredFilesIn(app));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(app.ClientUpdatesFolder, $"{sha256}.exe")));
        var listed = await ReleaseOfAsync(admin);
        Assert.Equal("0.20.0", listed.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Upload_LogsWhoWhenVersionAndHash()
    {
        var inspector = new FakeInstallerInspector();
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, inspector));
        using var admin = await SignedInAsync(app);
        var sha256 = Sha256Of(Installer("0.20.0"));

        await StoreReleaseAsync(admin, inspector, "0.20.0");

        Assert.Contains(app.Logs.Entries, entry => entry.StartsWith("Information:", StringComparison.Ordinal)
            && entry.Contains("0.20.0", StringComparison.Ordinal)
            && entry.Contains(sha256, StringComparison.Ordinal)
            && entry.Contains("uploaded by tester", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upload_WhenNotAWindowsProgram_IsRefused()
    {
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, new FakeInstallerInspector()));
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, "PK\x03\x04 a zip file"u8.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(WrongInstallerMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null(await StoredReleaseAsync(app));
        Assert.Empty(StoredFilesIn(app));
    }

    [Fact]
    public async Task Upload_EmptyBody_IsRefused()
    {
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, new FakeInstallerInspector()));
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(WrongInstallerMessage, (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Upload_OfAnotherProduct_IsRefused()
    {
        var inspector = new FakeInstallerInspector { Next = new InstallerInfo("Some Other Program", "9.0.0.0") };
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, inspector));
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, Installer("9.0.0"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(WrongInstallerMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Empty(StoredFilesIn(app));
    }

    [Fact]
    public async Task Upload_WhoseVersionResourceCannotBeRead_IsRefused()
    {
        var inspector = new FakeInstallerInspector { Next = null };
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, inspector));
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, Installer("0.20.0"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(WrongInstallerMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null(await StoredReleaseAsync(app));
    }

    [Theory]
    [InlineData("0.20")]
    [InlineData("0.20.x")]
    [InlineData("")]
    [InlineData("0.020.0")]
    [InlineData("0.20.0000000000")]
    public async Task Upload_WhoseVersionIsNotMajorMinorPatch_IsRefused(string fileVersion)
    {
        var inspector = new FakeInstallerInspector { Next = new InstallerInfo(ClientUpdateEndpoints.ProductName, fileVersion) };
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, inspector));
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, Installer("0.20.0"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(WrongInstallerMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null(await StoredReleaseAsync(app));
    }

    [Fact]
    public async Task Upload_OfTheSameVersion_ReplacesTheStoredInstaller()
    {
        var inspector = new FakeInstallerInspector();
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, inspector));
        using var admin = await SignedInAsync(app);
        await StoreReleaseAsync(admin, inspector, "0.20.0");
        var rebuilt = Installer("0.20.0").Concat(new byte[] { 1, 2, 3 }).ToArray();

        inspector.Next = new InstallerInfo(ClientUpdateEndpoints.ProductName, "0.20.0.0");
        var response = await UploadAsync(admin, rebuilt);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = (await StoredReleaseAsync(app))!;
        Assert.Equal("0.20.0", stored.Version);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(rebuilt)), stored.Sha256);
    }

    [Fact]
    public async Task Upload_OfAnOlderVersion_ReplacesTheStoredInstaller()
    {
        var inspector = new FakeInstallerInspector();
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, inspector));
        using var admin = await SignedInAsync(app);
        await StoreReleaseAsync(admin, inspector, "0.20.0");

        inspector.Next = new InstallerInfo(ClientUpdateEndpoints.ProductName, "0.19.0.0");
        var response = await UploadAsync(admin, Installer("0.19.0"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0.19.0", (await StoredReleaseAsync(app))!.Version);
    }

    [Fact]
    public async Task Upload_NewerRelease_ReplacesTheOldOne_AndRemovesItsFile()
    {
        var inspector = new FakeInstallerInspector();
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, inspector));
        using var admin = await SignedInAsync(app);
        var oldHash = await StoredHashAsync(admin, inspector, "0.20.0");

        var newHash = await StoredHashAsync(admin, inspector, "0.21.0");

        Assert.Equal([$"{newHash}.exe"], StoredFilesIn(app));
        Assert.Equal(1, await ReleaseRowCountAsync(app));
        Assert.Equal("0.21.0", (await StoredReleaseAsync(app))!.Version);
        Assert.NotEqual(oldHash, newHash);
    }

    [Fact]
    public async Task Upload_ReplacingARelease_ClearsThePendingTargetOfTheOldVersion()
    {
        var inspector = new FakeInstallerInspector();
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, inspector));
        using var admin = await SignedInAsync(app);
        await StoreReleaseAsync(admin, inspector, "0.20.0");
        var pc = await AddPcAsync(app, "PC-01");
        Assert.Equal(HttpStatusCode.OK, (await OfferStoredAsync(admin, pc.Id)).StatusCode);

        await StoreReleaseAsync(admin, inspector, "0.21.0");

        Assert.Null(await ClientUpdateOfListEntryAsync(admin, "PC-01"));
    }

    [Fact]
    public async Task Upload_WithAnInstallerTooLargeByDeclaredLength_IsRefusedBeforeTheBodyIsRead()
    {
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, new FakeInstallerInspector()));
        using var admin = await SignedInAsync(app);
        var content = new StreamContent(new InstallerBodyStream(InstallerLimitBytes + 1));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = InstallerLimitBytes + 1;

        var response = await admin.PutAsync(ReleaseRoute, content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(TooLargeMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null(await StoredReleaseAsync(app));
    }

    [Fact]
    public async Task Upload_WithAnInstallerTooLargeWhileStreaming_IsRefused_AndLeavesNoFile()
    {
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, new FakeInstallerInspector()));
        using var admin = await SignedInAsync(app);
        var content = new StreamContent(new InstallerBodyStream(InstallerLimitBytes + 1));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await admin.PutAsync(ReleaseRoute, content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(TooLargeMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Null(await StoredReleaseAsync(app));
        Assert.Empty(StoredFilesIn(app));
    }

    [Fact]
    public async Task Upload_ExactlyTheLimit_IsAccepted()
    {
        var inspector = new FakeInstallerInspector();
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, inspector));
        using var admin = await SignedInAsync(app);
        var content = new StreamContent(new InstallerBodyStream(InstallerLimitBytes));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await admin.PutAsync(ReleaseRoute, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(InstallerLimitBytes, (await JsonOf(response)).GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task Upload_WithoutSignIn_IsUnauthorized_AndStoresNothing()
    {
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, new FakeInstallerInspector()));
        using var anonymous = app.CreateBrowser();

        var response = await UploadAsync(anonymous, Installer("0.20.0"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(await StoredReleaseAsync(app));
    }

    [Fact]
    public async Task Upload_AboveTenPerHour_IsRefusedWithTooManyRequests()
    {
        var inspector = new FakeInstallerInspector();
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, inspector));
        using var admin = await SignedInAsync(app);
        for (var minor = 1; minor <= ClientUpdateEndpoints.MaxUploadsPerHour; minor++)
        {
            await StoreReleaseAsync(admin, inspector, $"0.{minor}.0");
        }

        inspector.Next = new InstallerInfo(ClientUpdateEndpoints.ProductName, "0.99.0.0");
        var response = await UploadAsync(admin, Installer("0.99.0"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("Too many installer uploads. Try again in an hour.", (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal($"0.{ClientUpdateEndpoints.MaxUploadsPerHour}.0", (await StoredReleaseAsync(app))!.Version);
    }

    [Fact]
    public async Task Upload_FromAnAnonymousCaller_DoesNotUseUpTheAdministratorsAllowance()
    {
        var inspector = new FakeInstallerInspector();
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, inspector));
        using var admin = await SignedInAsync(app);
        using var anonymous = app.CreateBrowser();
        for (var i = 0; i < ClientUpdateEndpoints.MaxUploadsPerHour + 1; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await UploadAsync(anonymous, Installer("0.20.0"))).StatusCode);
        }

        for (var minor = 1; minor <= ClientUpdateEndpoints.MaxUploadsPerHour; minor++)
        {
            await StoreReleaseAsync(admin, inspector, $"0.{minor}.0");
        }

        Assert.Equal($"0.{ClientUpdateEndpoints.MaxUploadsPerHour}.0", (await StoredReleaseAsync(app))!.Version);
    }

    [Fact]
    public async Task Upload_RemovesStrayFilesInTheFolder_KeepingOnlyTheNewRelease()
    {
        var inspector = new FakeInstallerInspector();
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, inspector));
        using var admin = await SignedInAsync(app);
        Directory.CreateDirectory(app.ClientUpdatesFolder);
        await File.WriteAllBytesAsync(Path.Combine(app.ClientUpdatesFolder, "stray.exe"), Installer("0.0.1"));
        var crashed = Path.Combine(app.ClientUpdatesFolder, "upload-left-behind.tmp");
        await File.WriteAllBytesAsync(crashed, Installer("0.0.2"));
        File.SetLastWriteTimeUtc(crashed, app.Time.GetUtcNow().AddHours(-2).UtcDateTime);

        var sha256 = await StoredHashAsync(admin, inspector, "0.20.0");

        Assert.Equal([$"{sha256}.exe"], StoredFilesIn(app));
    }

    [Fact]
    public async Task Delete_RemovesTheRelease_AndItsFile_AndLogs()
    {
        var inspector = new FakeInstallerInspector();
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, inspector));
        using var admin = await SignedInAsync(app);
        await StoreReleaseAsync(admin, inspector, "0.20.0");

        var response = await admin.DeleteAsync(ReleaseRoute);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(await StoredReleaseAsync(app));
        Assert.Empty(StoredFilesIn(app));
        Assert.Equal(JsonValueKind.Null, (await ReleaseOfAsync(admin)).ValueKind);
        Assert.Contains(app.Logs.Entries, entry => entry.Contains("Client installer 0.20.0 deleted by tester", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_WhenNothingIsStored_IsStillNoContent()
    {
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, new FakeInstallerInspector()));
        using var admin = await SignedInAsync(app);

        var first = await admin.DeleteAsync(ReleaseRoute);
        var second = await admin.DeleteAsync(ReleaseRoute);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    [Fact]
    public async Task Delete_ClearsEveryPendingUpdate()
    {
        var inspector = new FakeInstallerInspector();
        using var app = new TestApp(configureServices: services => ReplaceInspector(services, inspector));
        using var admin = await SignedInAsync(app);
        await StoreReleaseAsync(admin, inspector, "0.20.0");
        var pc = await AddPcAsync(app, "PC-01");
        await OfferStoredAsync(admin, pc.Id);

        await admin.DeleteAsync(ReleaseRoute);

        Assert.Null(await ClientUpdateOfListEntryAsync(admin, "PC-01"));
    }

    [Fact]
    public async Task Upload_TheRealInstaller_IsAcceptedWithTheVersionItCarries()
    {
        var path = Environment.GetEnvironmentVariable("LYKANTRA_TEST_INSTALLER") ?? RealInstallerPath;
        if (!File.Exists(path))
        {
            throw SkipException.ForSkip($"The real installer isn't at {path}. Set LYKANTRA_TEST_INSTALLER to test with it.");
        }

        // The real version resource is read, not the fake one.
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var bytes = await File.ReadAllBytesAsync(path);

        var response = await UploadAsync(admin, bytes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        // Whatever version the installer carries now, the server reads that same version.
        var expected = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion!.Trim();
        Assert.Equal(string.Join('.', expected.Split('.').Take(3)), body.GetProperty("version").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), body.GetProperty("sha256").GetString());
        Assert.Equal(bytes.Length, body.GetProperty("sizeBytes").GetInt64());
    }

    // Schema

    [Fact]
    public async Task Migration_AddClientUpdates_IsAppliedAndAddsTheTableAndPcColumns()
    {
        using var app = new TestApp();

        var (applied, pending, releaseColumns, pcColumns) = await app.WithDbAsync(async db => (
            await db.Database.GetAppliedMigrationsAsync(),
            await db.Database.GetPendingMigrationsAsync(),
            await db.Database.SqlQueryRaw<string>("SELECT name AS \"Value\" FROM pragma_table_info('ClientRelease')").ToListAsync(),
            await db.Database.SqlQueryRaw<string>("SELECT name AS \"Value\" FROM pragma_table_info('Pcs')").ToListAsync()));

        Assert.Contains(applied, id => id.EndsWith("_AddClientUpdates", StringComparison.Ordinal));
        Assert.Empty(pending);
        Assert.Contains("Sha256", releaseColumns);
        Assert.Contains("FileName", releaseColumns);
        Assert.Contains("ClientVersion", pcColumns);
        Assert.Contains("ClientUpdateTargetVersion", pcColumns);
        Assert.Contains("ClientUpdateState", pcColumns);
        Assert.Contains("ClientUpdateError", pcColumns);
        Assert.Contains("ClientUpdateStateAtUtc", pcColumns);
    }

    private static void ReplaceInspector(IServiceCollection services, IInstallerInspector inspector)
    {
        services.RemoveAll<IInstallerInspector>();
        services.AddSingleton<IInstallerInspector>(inspector);
    }
}
