using System.Net;
using LabGuard.Server.SharedFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;
using static LabGuard.Server.Tests.SharedFileTestSupport;

namespace LabGuard.Server.Tests;

// The administrator uploads a file to send to PCs. It is streamed to disk under its hash, stored once, and refused when the
// name is not a valid Windows file name or the disk would get too full.
public class SharedFileEndpointTests
{
    private static TestApp AppWithDisk(FakeDiskSpace disk) =>
        new(configureServices: services =>
        {
            services.RemoveAll<IDiskSpace>();
            services.AddSingleton<IDiskSpace>(disk);
        });

    // Access

    [Fact]
    public async Task Upload_WithoutCookie_Returns401AndStoresNothing()
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();

        var response = await UploadAsync(browser, Bytes("abc"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(StoredFilesIn(app));
    }

    [Fact]
    public async Task Upload_FromThePcPort_Returns404()
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient();

        var response = await UploadAsync(pcClient, Bytes("abc"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(StoredFilesIn(app));
    }

    // Storing

    [Fact]
    public async Task Upload_Returns201WithIdNameSizeAndHash_AndStoresTheBytesUnderTheHash()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var bytes = Bytes("hello class");

        var response = await UploadAsync(admin, bytes, "notes.pdf");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonOf(response);
        Assert.True(body.GetProperty("id").GetInt32() > 0);
        Assert.Equal("notes.pdf", body.GetProperty("name").GetString());
        Assert.Equal(bytes.Length, body.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(Sha256Of(bytes), body.GetProperty("sha256").GetString());
        Assert.Equal([Sha256Of(bytes)], StoredFilesIn(app));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(app.SharedFilesFolder, Sha256Of(bytes))));
    }

    [Fact]
    public async Task Upload_DecodesTheName_AndKeepsOnlyItsLastPart()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, Bytes("abc"), Uri.EscapeDataString("..\\..\\Windows\\Résumé (1).pdf"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Résumé (1).pdf", (await JsonOf(response)).GetProperty("name").GetString());
        Assert.Equal([Sha256Of(Bytes("abc"))], StoredFilesIn(app));
    }

    // Exactly what the dashboard's XMLHttpRequest sends: the session cookie, the octet-stream type and the name as
    // encodeURIComponent leaves it (which keeps ( ) ! ' * as they are). No other header is needed.
    [Fact]
    public async Task Upload_AsTheDashboardSendsIt_IsAccepted()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, Bytes("abc"), "Week%202%20(final)!%20%E2%80%93%20r%C3%A9sum%C3%A9.pdf");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Week 2 (final)! – résumé.pdf", (await JsonOf(response)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Upload_EmptyFile_IsStored()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, []);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, (await JsonOf(response)).GetProperty("sizeBytes").GetInt64());
    }

    [Fact]
    public async Task Upload_SameBytesAgain_ReusesTheRow_WithTheNewNameAndTime()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var bytes = Bytes("same bytes");
        var first = await JsonOf(await UploadAsync(admin, bytes, "first.txt"));
        app.Time.Advance(TimeSpan.FromMinutes(30));
        // Signed in again: the first sign-in has passed its idle limit.
        using var laterAdmin = await SignedInAsync(app);

        var second = await JsonOf(await UploadAsync(laterAdmin, bytes, "second.txt"));

        Assert.Equal(first.GetProperty("id").GetInt32(), second.GetProperty("id").GetInt32());
        Assert.Equal("second.txt", second.GetProperty("name").GetString());
        var stored = await app.WithDbAsync(db => db.SharedFiles.AsNoTracking().SingleAsync());
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.CreatedUtc.ToUnixTimeMilliseconds());
        Assert.Equal([Sha256Of(bytes)], StoredFilesIn(app));
    }

    [Fact]
    public async Task Upload_SameBytesWhoseFileWentMissing_PutsTheFileBack()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var bytes = Bytes("restore me");
        await UploadIdAsync(admin, bytes);
        File.Delete(Path.Combine(app.SharedFilesFolder, Sha256Of(bytes)));

        await UploadIdAsync(admin, bytes);

        Assert.Equal([Sha256Of(bytes)], StoredFilesIn(app));
    }

    [Fact]
    public async Task Upload_DifferentBytes_GetTheirOwnRows()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var first = await UploadIdAsync(admin, Bytes("one"));
        var second = await UploadIdAsync(admin, Bytes("two"));

        Assert.NotEqual(first, second);
        Assert.Equal(2, StoredFilesIn(app).Length);
    }

    // Refusals

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CON")]
    [InlineData("bad%7Cname.txt")]
    [InlineData("folder%2F")]
    [InlineData("name.")]
    [InlineData("a%00b.txt")]
    public async Task Upload_InvalidName_Returns400AndStoresNothing(string? encodedName)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, Bytes("abc"), encodedName);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidFileNameMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Empty(StoredFilesIn(app));
        Assert.Equal(0, await app.WithDbAsync(db => db.SharedFiles.CountAsync()));
    }

    [Fact]
    public async Task Upload_NameTooLong_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, Bytes("abc"), new string('a', 201));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("multipart/form-data")]
    [InlineData("application/json")]
    public async Task Upload_NotOctetStream_Returns400(string contentType)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, Bytes("abc"), "notes.txt", contentType);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(StoredFilesIn(app));
    }

    [Fact]
    public async Task Upload_DeclaredLengthWouldLeaveLessThan1GB_Returns507BeforeReading()
    {
        var disk = new FakeDiskSpace { AvailableFor = _ => SharedFileStorage.MinFreeBytes + 10 };
        using var app = AppWithDisk(disk);
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, new byte[11]);

        Assert.Equal((HttpStatusCode)507, response.StatusCode);
        Assert.Equal(NoDiskSpaceMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Empty(StoredFilesIn(app));
    }

    [Fact]
    public async Task Upload_DeclaredLengthThatStillLeaves1GB_IsStored()
    {
        var disk = new FakeDiskSpace { AvailableFor = _ => SharedFileStorage.MinFreeBytes + 10 };
        using var app = AppWithDisk(disk);
        using var admin = await SignedInAsync(app);

        var response = await UploadAsync(admin, new byte[10]);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Upload_DiskFillsUpWhileStreaming_Returns507AndRemovesTheTempFile()
    {
        // Plenty of room when the upload starts, then none at the first check while it streams in.
        var disk = new FakeDiskSpace { AvailableFor = call => call == 1 ? long.MaxValue / 2 : 0 };
        using var app = AppWithDisk(disk);
        using var admin = await SignedInAsync(app);

        var response = await UploadContentAsync(admin, new StreamContent(new GeneratedBodyStream(20L * 1024 * 1024)), "big.bin");

        Assert.Equal((HttpStatusCode)507, response.StatusCode);
        Assert.True(disk.Calls >= 2);
        Assert.Empty(StoredFilesIn(app));
        Assert.Equal(0, await app.WithDbAsync(db => db.SharedFiles.CountAsync()));
    }

    [Fact]
    public async Task Upload_LargerThanKestrelsDefaultLimit_IsStreamedAndStored()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await UploadContentAsync(admin, new StreamContent(new GeneratedBodyStream(40L * 1024 * 1024)), "big.bin");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(40L * 1024 * 1024, (await JsonOf(response)).GetProperty("sizeBytes").GetInt64());
        Assert.Single(StoredFilesIn(app));
    }

    // Helpers

    [Theory]
    [InlineData("application/octet-stream", true)]
    [InlineData("Application/Octet-Stream; charset=binary", true)]
    [InlineData("text/plain", false)]
    [InlineData(null, false)]
    public void IsOctetStream_ReadsTheMediaType(string? contentType, bool expected) =>
        Assert.Equal(expected, SharedFileEndpoints.IsOctetStream(contentType));

    [Fact]
    public void TryReadFileName_DecodesAndCleans()
    {
        Assert.True(SharedFileEndpoints.TryReadFileName("a%2Fb%20c.txt", out var name));
        Assert.Equal("b c.txt", name);
        Assert.False(SharedFileEndpoints.TryReadFileName(new string('a', 2401), out _));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070), true)]
    [InlineData(unchecked((int)0x80070027), true)]
    [InlineData(unchecked((int)0x80070020), false)]
    [InlineData(unchecked((int)0x80131620), false)]
    public void IsDiskFull_RecognizesTheDiskFullErrors(int hresult, bool expected) =>
        Assert.Equal(expected, SharedFileEndpoints.IsDiskFull(new IOException("disk", hresult)));

    [Fact]
    public void Storage_StoredFiles_ListsOnlyHashNamedFiles()
    {
        using var app = new TestApp();
        var storage = app.Services.GetRequiredService<SharedFileStorage>();
        var hash = new string('c', 64);
        Directory.CreateDirectory(app.SharedFilesFolder);
        File.WriteAllText(Path.Combine(app.SharedFilesFolder, hash), "x");
        File.WriteAllText(storage.NewTempPath(), "x");
        File.WriteAllText(Path.Combine(app.SharedFilesFolder, "notes.txt"), "x");

        Assert.Equal([hash], storage.StoredFiles().Select(file => file.Sha256));
    }

    [Fact]
    public void Storage_TryDelete_SaysWhetherAFileWasRemoved()
    {
        using var app = new TestApp();
        var storage = app.Services.GetRequiredService<SharedFileStorage>();
        var path = storage.NewTempPath();
        File.WriteAllText(path, "x");

        Assert.True(storage.TryDelete(path));
        Assert.False(storage.TryDelete(path));
    }

    [Fact]
    public void Storage_PathFor_AcceptsOnlyAHash()
    {
        using var app = new TestApp();
        var storage = app.Services.GetRequiredService<SharedFileStorage>();
        var hash = new string('a', 64);

        Assert.Equal(Path.Combine(app.SharedFilesFolder, hash), storage.PathFor(hash));
        Assert.Throws<ArgumentException>(() => storage.PathFor("..\\..\\secret"));
        Assert.Throws<ArgumentException>(() => storage.PathFor(new string('A', 64)));
        Assert.Throws<ArgumentException>(() => storage.PathFor(hash + "\n"));
    }

    [Fact]
    public void Storage_SweepStaleTemps_RemovesOnlyOldTempFiles()
    {
        using var app = new TestApp();
        var storage = app.Services.GetRequiredService<SharedFileStorage>();
        var stale = storage.NewTempPath();
        var fresh = storage.NewTempPath();
        var stored = Path.Combine(app.SharedFilesFolder, new string('b', 64));
        File.WriteAllText(stale, "x");
        File.WriteAllText(fresh, "x");
        File.WriteAllText(stored, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(2));
        File.SetLastWriteTimeUtc(stored, DateTime.UtcNow - TimeSpan.FromHours(2));

        storage.SweepStaleTemps(DateTimeOffset.UtcNow);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(stored));
    }

    [Fact]
    public void Storage_HasRoomFor_KeepsTheMinimumFree()
    {
        var disk = new FakeDiskSpace { AvailableFor = _ => SharedFileStorage.MinFreeBytes + 100 };
        using var app = AppWithDisk(disk);
        var storage = app.Services.GetRequiredService<SharedFileStorage>();

        Assert.True(storage.HasRoomFor(100));
        Assert.False(storage.HasRoomFor(101));
    }
}
