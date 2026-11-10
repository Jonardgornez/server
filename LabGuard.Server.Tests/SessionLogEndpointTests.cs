using System.Net;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Data;
using LabGuard.Server.Logs;
using LabGuard.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

public class SessionLogEndpointTests
{
    private const string ListPath = "/api/session-logs";
    private const string ExportPath = "/api/session-logs/export";
    private const string Csv = "PC,User or source,Start,End,Duration (minutes),Ended by\r\n";

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo PlusTen = TimeZoneInfo.CreateCustomTimeZone("UTC+10", TimeSpan.FromHours(10), "UTC+10", "UTC+10");

    // Local times in the server zone (UTC+10), so the day edges fall where the filters expect them.
    private static DateTimeOffset Local(int day, int hour = 0, int minute = 0, int second = 0, int millisecond = 0) =>
        new(2026, 11, day, hour, minute, second, millisecond, TimeSpan.FromHours(10));

    private static PcSession Ended(string pcName, string username, DateTimeOffset startedAt, DateTimeOffset endedAt, string reason = SessionLifecycle.Logout) => new()
    {
        PcId = 0,
        PcName = pcName,
        Username = username,
        UserAccountId = 1,
        StartedAtUtc = startedAt,
        EndedAtUtc = endedAt,
        EndReason = reason,
    };

    // A running session needs its own PC, since the database allows one active session per PC.
    private static PcSession Running(int pcId, string pcName, string username, DateTimeOffset startedAt) => new()
    {
        PcId = pcId,
        PcName = pcName,
        Username = username,
        UserAccountId = 1,
        StartedAtUtc = startedAt,
    };

    private static PcSession AdminUnlock(int pcId, string pcName, DateTimeOffset startedAt) => new()
    {
        PcId = pcId,
        PcName = pcName,
        Username = SessionLifecycle.AdminUnlockUsername,
        UserAccountId = SessionLifecycle.AdminUnlockAccountId,
        StartedAtUtc = startedAt,
        IsAdminUnlock = true,
    };

    private static PcSession LoginRequestSession(string pcName, DateTimeOffset startedAt, DateTimeOffset endedAt, string reason) => new()
    {
        PcId = 0,
        PcName = pcName,
        Username = SessionLifecycle.LoginRequestUsername,
        UserAccountId = SessionLifecycle.AdminUnlockAccountId,
        StartedAtUtc = startedAt,
        EndedAtUtc = endedAt,
        EndReason = reason,
        LoginRequestId = 5,
    };

    private static async Task SaveAsync(TestApp app, IEnumerable<PcSession> sessions)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PcSessions.AddRange(sessions);
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static string[] PcNamesOf(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("pcName").GetString()!).ToArray();

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string message)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(message, (await JsonOf(response)).GetProperty("error").GetString());
    }

    private static TestApp NewApp()
    {
        var app = new TestApp(startUtc: Now);
        app.Time.SetLocalTimeZone(PlusTen);
        return app;
    }

    // Access

    [Fact]
    public async Task Requests_WithoutSignIn_AreRefused()
    {
        using var app = NewApp();
        using var browser = app.CreateBrowser();

        await AssertErrorAsync(await browser.GetAsync(ListPath), HttpStatusCode.Unauthorized, "Please sign in.");
        await AssertErrorAsync(await browser.GetAsync(ExportPath), HttpStatusCode.Unauthorized, "Please sign in.");
    }

    [Fact]
    public async Task Requests_OnThePcPort_AreNotFound()
    {
        using var app = NewApp();
        using var pc = app.CreatePcClient();

        Assert.Equal(HttpStatusCode.NotFound, (await pc.GetAsync(ListPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pc.GetAsync(ExportPath)).StatusCode);
    }

    // List: shape and mapping

    [Fact]
    public async Task List_ReturnsEachSessionWithItsSourceDurationAndEndReason()
    {
        using var app = NewApp();
        await SaveAsync(app,
        [
            Ended("PC-01", "student", Local(9, 8), Local(9, 9, 30)),
            AdminUnlock(2, "PC-02", Local(9, 11)),
            LoginRequestSession("PC-03", Local(9, 9), Local(9, 9, 15), SessionLifecycle.TimeExpired),
        ]);
        using var admin = await app.SignedInAdminAsync();

        var response = await admin.GetAsync(ListPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(
            new[] { "items", "page", "pageSize", "totalItems" },
            body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(3, body.GetProperty("totalItems").GetInt32());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(10, body.GetProperty("pageSize").GetInt32());

        var items = body.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("pcName").GetString()!);
        Assert.Equal(
            new[] { "id", "pcName", "userOrSource", "source", "startedAt", "endedAt", "durationMinutes", "endReason" },
            items["PC-01"].EnumerateObject().Select(p => p.Name).ToArray());

        var running = items["PC-02"];
        Assert.Equal("Admin unlock", running.GetProperty("userOrSource").GetString());
        Assert.Equal("admin-unlock", running.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, running.GetProperty("endedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, running.GetProperty("endReason").ValueKind);
        Assert.Equal(660, running.GetProperty("durationMinutes").GetInt32());

        var account = items["PC-01"];
        Assert.Equal("student", account.GetProperty("userOrSource").GetString());
        Assert.Equal("account", account.GetProperty("source").GetString());
        Assert.Equal(Local(9, 8), account.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(Local(9, 9, 30), account.GetProperty("endedAt").GetDateTimeOffset());
        Assert.Equal(90, account.GetProperty("durationMinutes").GetInt32());
        Assert.Equal("logout", account.GetProperty("endReason").GetString());

        var loginRequest = items["PC-03"];
        Assert.Equal("login-request", loginRequest.GetProperty("source").GetString());
        Assert.Equal("time-expired", loginRequest.GetProperty("endReason").GetString());
    }

    // List: ordering

    [Fact]
    public async Task List_RunningSessionsComeFirst_ThenNewestStart_ThenNewestId()
    {
        using var app = NewApp();
        // Inserted in this order, so the later rows have the higher ids.
        await SaveAsync(app,
        [
            Running(1, "Run-Old", "a", Local(8, 8)),
            Ended("Ended-Old", "a", Local(8, 9), Local(8, 9, 10)),
            Ended("Same-First", "a", Local(9, 9), Local(9, 9, 5)),
            Ended("Same-Second", "a", Local(9, 9), Local(9, 9, 5)),
            Running(2, "Run-New", "a", Local(9, 10)),
        ]);
        using var admin = await app.SignedInAdminAsync();

        var names = PcNamesOf(await JsonOf(await admin.GetAsync(ListPath)));

        Assert.Equal(new[] { "Run-New", "Run-Old", "Same-Second", "Same-First", "Ended-Old" }, names);
    }

    // List: local day edges

    [Fact]
    public async Task List_DateRange_IncludesWholeLocalDays_EvenAcrossUtcMidnight()
    {
        using var app = NewApp();
        await SaveAsync(app,
        [
            Ended("Before", "a", Local(7, 23, 59, 59, 999), Local(7, 23, 59, 59, 999).AddMinutes(1)),
            Ended("Midnight", "a", Local(8), Local(8, 0, 1)),
            Ended("Last-Moment", "a", Local(8, 23, 59, 59, 999), Local(9)),
            Ended("Next-Midnight", "a", Local(9), Local(9, 0, 1)),
            Ended("After", "a", Local(10), Local(10, 0, 1)),
        ]);
        using var admin = await app.SignedInAdminAsync();

        Assert.Equal(
            new[] { "Last-Moment", "Midnight" },
            PcNamesOf(await JsonOf(await admin.GetAsync($"{ListPath}?from=2026-11-08&to=2026-11-08"))));
        Assert.Equal(
            new[] { "Next-Midnight", "Last-Moment", "Midnight" },
            PcNamesOf(await JsonOf(await admin.GetAsync($"{ListPath}?from=2026-11-08&to=2026-11-09"))));
        Assert.Equal(
            new[] { "Before" },
            PcNamesOf(await JsonOf(await admin.GetAsync($"{ListPath}?to=2026-11-07"))));
        Assert.Equal(
            new[] { "After", "Next-Midnight" },
            PcNamesOf(await JsonOf(await admin.GetAsync($"{ListPath}?from=2026-11-09"))));
    }

    // List: text filters

    [Fact]
    public async Task List_PcAndUserFilters_AreCaseInsensitiveContainsAndLiteral()
    {
        using var app = NewApp();
        await SaveAsync(app,
        [
            Ended("Lab-Room-1", "alice.smith", Local(9, 8), Local(9, 9)),
            Ended("Office", "axb", Local(9, 10), Local(9, 11)),
            Ended("Other", "a_b", Local(9, 12), Local(9, 13)),
            Ended("PC-50%", "carol", Local(9, 14), Local(9, 15)),
            Ended("PC-500", "dave", Local(9, 16), Local(9, 17)),
        ]);
        using var admin = await app.SignedInAdminAsync();

        Assert.Equal(new[] { "Lab-Room-1" }, PcNamesOf(await JsonOf(await admin.GetAsync($"{ListPath}?pc=LAB"))));
        Assert.Equal(new[] { "Lab-Room-1" }, PcNamesOf(await JsonOf(await admin.GetAsync($"{ListPath}?user=ALI"))));
        Assert.Equal(new[] { "Lab-Room-1" }, PcNamesOf(await JsonOf(await admin.GetAsync($"{ListPath}?pc=%20lab%20"))));
        Assert.Equal(new[] { "PC-50%" }, PcNamesOf(await JsonOf(await admin.GetAsync($"{ListPath}?pc=%25"))));
        Assert.Equal(new[] { "Other" }, PcNamesOf(await JsonOf(await admin.GetAsync($"{ListPath}?user=_"))));
        Assert.Empty(PcNamesOf(await JsonOf(await admin.GetAsync($"{ListPath}?pc=lab&user=bob"))));
    }

    // List: paging

    [Fact]
    public async Task List_PagesAndClampsAPageBeyondTheLastOne()
    {
        using var app = NewApp();
        await SaveAsync(app, Enumerable.Range(1, 5).Select(i => Ended($"S{i}", "a", Local(9, i), Local(9, i, 1))));
        using var admin = await app.SignedInAdminAsync();

        var second = await JsonOf(await admin.GetAsync($"{ListPath}?page=2&pageSize=2"));
        Assert.Equal(new[] { "S3", "S2" }, PcNamesOf(second));
        Assert.Equal(5, second.GetProperty("totalItems").GetInt32());
        Assert.Equal(2, second.GetProperty("page").GetInt32());

        var third = await JsonOf(await admin.GetAsync($"{ListPath}?page=3&pageSize=2"));
        Assert.Equal(new[] { "S1" }, PcNamesOf(third));

        var beyond = await JsonOf(await admin.GetAsync($"{ListPath}?page=9&pageSize=2"));
        Assert.Equal(new[] { "S1" }, PcNamesOf(beyond));
        Assert.Equal(3, beyond.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task List_NoMatches_IsPageOneAndEmpty()
    {
        using var app = NewApp();
        using var admin = await app.SignedInAdminAsync();

        var body = await JsonOf(await admin.GetAsync($"{ListPath}?pc=nothing&page=4"));

        Assert.Empty(PcNamesOf(body));
        Assert.Equal(0, body.GetProperty("totalItems").GetInt32());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task List_WithoutPaging_ReturnsTheFirstTenOfTwelve()
    {
        using var app = NewApp();
        await SaveAsync(app, Enumerable.Range(1, 12).Select(i => Ended($"S{i:00}", "a", Local(9, 0, i), Local(9, 0, i, 1))));
        using var admin = await app.SignedInAdminAsync();

        var body = await JsonOf(await admin.GetAsync(ListPath));

        Assert.Equal(10, PcNamesOf(body).Length);
        Assert.Equal(12, body.GetProperty("totalItems").GetInt32());
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(10, body.GetProperty("pageSize").GetInt32());
    }

    // Validation

    [Theory]
    [InlineData("?from=2026-13-01", "From must be a date in the form yyyy-MM-dd.")]
    [InlineData("?to=nope", "To must be a date in the form yyyy-MM-dd.")]
    [InlineData("?from=2026-11-10&to=2026-11-09", "The From date can't be after the To date.")]
    [InlineData("?page=0", "Page must be a whole number, 1 or more.")]
    [InlineData("?page=abc", "Page must be a whole number, 1 or more.")]
    [InlineData("?pageSize=0", "Page size must be a whole number from 1 to 100.")]
    [InlineData("?pageSize=101", "Page size must be a whole number from 1 to 100.")]
    public async Task List_InvalidValues_AreRefusedWithAPlainMessage(string query, string message)
    {
        using var app = NewApp();
        using var admin = await app.SignedInAdminAsync();

        await AssertErrorAsync(await admin.GetAsync(ListPath + query), HttpStatusCode.BadRequest, message);
    }

    [Fact]
    public async Task List_OverLongFilters_AreRefused()
    {
        using var app = NewApp();
        using var admin = await app.SignedInAdminAsync();
        var tooLong = new string('x', 65);

        await AssertErrorAsync(await admin.GetAsync($"{ListPath}?pc={tooLong}"), HttpStatusCode.BadRequest, "The PC filter can be at most 64 characters.");
        await AssertErrorAsync(await admin.GetAsync($"{ListPath}?user={tooLong}"), HttpStatusCode.BadRequest, "The user filter can be at most 64 characters.");
    }

    // Export

    [Fact]
    public async Task Export_ReturnsCsvWithBomAttachmentNameAndRowsInListOrder()
    {
        using var app = NewApp();
        app.Time.SetUtcNow(new DateTimeOffset(2026, 11, 9, 14, 5, 0, TimeSpan.Zero));
        await SaveAsync(app,
        [
            Ended("PC-02", "bob", Local(9, 9), Local(9, 9, 45)),
            Running(1, "PC-01", "alice", Local(9, 8)),
        ]);
        using var admin = await app.SignedInAdminAsync();

        var response = await admin.GetAsync(ExportPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("attachment; filename=\"session-logs-20261110-0005.csv\"", response.Content.Headers.GetValues("Content-Disposition").Single());
        Assert.False(response.Headers.Contains(SessionLogEndpoints.ExportTruncatedHeader));

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Equal(
            Csv
            + "PC-01,alice,2026-11-09 08:00,,965,Running\r\n"
            + "PC-02,bob,2026-11-09 09:00,2026-11-09 09:45,45,Logout\r\n",
            Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public async Task Export_AppliesTheSameFilters()
    {
        using var app = NewApp();
        await SaveAsync(app,
        [
            Ended("PC-02", "bob", Local(9, 9), Local(9, 9, 45)),
            Ended("PC-03", "carol", Local(9, 10), Local(9, 10, 5)),
        ]);
        using var admin = await app.SignedInAdminAsync();

        var text = await admin.GetStringAsync($"{ExportPath}?user=BOB");

        Assert.Equal(Csv + "PC-02,bob,2026-11-09 09:00,2026-11-09 09:45,45,Logout\r\n", text.TrimStart('﻿'));
    }

    [Fact]
    public async Task Export_IgnoresThePagingValues()
    {
        using var app = NewApp();
        using var admin = await app.SignedInAdminAsync();

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{ExportPath}?page=0&pageSize=500")).StatusCode);
    }

    [Theory]
    [InlineData("?from=2026-02-30")]
    [InlineData("?to=2026-11-09&from=2026-11-10")]
    public async Task Export_InvalidFilters_AreRefused(string query)
    {
        using var app = NewApp();
        using var admin = await app.SignedInAdminAsync();

        var response = await admin.GetAsync(ExportPath + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Export_CellsThatLookLikeFormulas_AreNeutralised()
    {
        using var app = NewApp();
        await SaveAsync(app, [Ended("=1+1, \"x\"", "@cmd", Local(9, 8), Local(9, 8, 1))]);
        using var admin = await app.SignedInAdminAsync();

        var text = await admin.GetStringAsync(ExportPath);

        Assert.Contains("\r\n\"'=1+1, \"\"x\"\"\",'@cmd,2026-11-09 08:00,2026-11-09 08:01,1,Logout\r\n", text);
    }

    [Fact]
    public async Task Export_OverTheRowLimit_ReturnsTheNewestRowsAndSetsTheTruncatedHeader()
    {
        using var app = NewApp();
        var first = Local(1);
        var rows = Enumerable.Range(0, SessionLogEndpoints.MaxExportRows)
            .Select(i => Ended("Bulk", "a", first.AddMinutes(i + 1), first.AddMinutes(i + 2)))
            .Append(Ended("Oldest-Row", "a", first, first.AddMinutes(1)));
        await SaveAsync(app, rows);
        using var admin = await app.SignedInAdminAsync();

        var response = await admin.GetAsync(ExportPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("true", response.Headers.GetValues(SessionLogEndpoints.ExportTruncatedHeader).Single());
        var lines = (await response.Content.ReadAsStringAsync()).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(SessionLogEndpoints.MaxExportRows + 1, lines.Length);
        Assert.DoesNotContain(lines, line => line.StartsWith("Oldest-Row,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_ExactlyTheRowLimit_IsNotTruncated()
    {
        using var app = NewApp();
        var first = Local(1);
        var rows = Enumerable.Range(0, SessionLogEndpoints.MaxExportRows)
            .Select(i => Ended("Bulk", "a", first.AddMinutes(i), first.AddMinutes(i + 1)));
        await SaveAsync(app, rows);
        using var admin = await app.SignedInAdminAsync();

        var response = await admin.GetAsync(ExportPath);

        Assert.False(response.Headers.Contains(SessionLogEndpoints.ExportTruncatedHeader));
        var lines = (await response.Content.ReadAsStringAsync()).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(SessionLogEndpoints.MaxExportRows + 1, lines.Length);
    }
}
