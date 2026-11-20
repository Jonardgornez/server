using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LabGuard.Server.Data;
using LabGuard.Server.Logs;
using LabGuard.Server.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

// "Purge old logs": the preview count and the delete agree, only ended sessions older than the choice go, and only the allowed choices work.
public class SessionLogPurgeTests
{
    private const string PreviewPath = "/api/session-logs/purge-preview";
    private const string PurgePath = "/api/session-logs/purge";

    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static PcSession Session(string pcName, int daysAgo, bool isRunning = false, int pcId = 0) => new()
    {
        PcId = pcId,
        PcName = pcName,
        Username = "student",
        UserAccountId = 1,
        StartedAtUtc = Now - TimeSpan.FromDays(daysAgo),
        EndedAtUtc = isRunning ? null : Now - TimeSpan.FromDays(daysAgo) + TimeSpan.FromHours(1),
        EndReason = isRunning ? null : SessionLifecycle.Logout,
    };

    private static async Task SaveAsync(TestApp app, params PcSession[] sessions)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.PcSessions.AddRange(sessions);
        await db.SaveChangesAsync();
    }

    private static Task<string[]> RemainingPcNamesAsync(TestApp app) =>
        app.WithDbAsync(db => db.PcSessions.AsNoTracking().OrderBy(s => s.PcName).Select(s => s.PcName).ToArrayAsync());

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static async Task AssertBadRequestAsync(HttpResponseMessage response, string message)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(message, (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Requests_WithoutSignIn_AreRefused()
    {
        using var app = new TestApp(startUtc: Now);
        using var browser = app.CreateBrowser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync($"{PreviewPath}?olderThanDays=30")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync(PurgePath, new { olderThanDays = 30 })).StatusCode);
    }

    [Fact]
    public async Task PreviewAndPurge_RemoveOnlyEndedSessionsOlderThanTheChoice()
    {
        using var app = new TestApp(startUtc: Now);
        await SaveAsync(app,
            Session("Old-1", 31),
            Session("Old-2", 200),
            Session("Exactly-30", 30),
            Session("Recent", 2),
            Session("Old-Running", 40, isRunning: true, pcId: 7));
        using var admin = await app.SignedInAdminAsync();

        var preview = await JsonOf(await admin.GetAsync($"{PreviewPath}?olderThanDays=30"));
        Assert.Equal(2, preview.GetProperty("count").GetInt32());

        var purge = await admin.PostAsJsonAsync(PurgePath, new { olderThanDays = 30 });

        Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
        Assert.Equal(2, (await JsonOf(purge)).GetProperty("deleted").GetInt32());
        Assert.Equal(["Exactly-30", "Old-Running", "Recent"], await RemainingPcNamesAsync(app));
    }

    [Fact]
    public async Task Purge_ALongerChoice_KeepsMore()
    {
        using var app = new TestApp(startUtc: Now);
        await SaveAsync(app, Session("Old-1", 100), Session("Old-2", 400));
        using var admin = await app.SignedInAdminAsync();

        var purge = await admin.PostAsJsonAsync(PurgePath, new { olderThanDays = 365 });

        Assert.Equal(1, (await JsonOf(purge)).GetProperty("deleted").GetInt32());
        Assert.Equal(["Old-1"], await RemainingPcNamesAsync(app));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?olderThanDays=")]
    [InlineData("?olderThanDays=7")]
    [InlineData("?olderThanDays=-30")]
    [InlineData("?olderThanDays=30.5")]
    [InlineData("?olderThanDays=abc")]
    public async Task Preview_OtherThanTheAllowedChoices_IsRefused(string query)
    {
        using var app = new TestApp(startUtc: Now);
        using var admin = await app.SignedInAdminAsync();

        await AssertBadRequestAsync(await admin.GetAsync(PreviewPath + query), SessionLogPurge.DaysMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(-90)]
    public async Task Purge_OtherThanTheAllowedChoices_IsRefusedAndDeletesNothing(int days)
    {
        using var app = new TestApp(startUtc: Now);
        await SaveAsync(app, Session("Old", 400));
        using var admin = await app.SignedInAdminAsync();

        await AssertBadRequestAsync(await admin.PostAsJsonAsync(PurgePath, new { olderThanDays = days }), SessionLogPurge.DaysMessage);
        Assert.Equal(["Old"], await RemainingPcNamesAsync(app));
    }

    [Fact]
    public async Task Purge_WithoutAChoice_IsRefused()
    {
        using var app = new TestApp(startUtc: Now);
        using var admin = await app.SignedInAdminAsync();

        await AssertBadRequestAsync(await admin.PostAsJsonAsync(PurgePath, new { }), SessionLogPurge.DaysMessage);
    }

    // A cross-site form can't send JSON, so a purge sent any other way is refused.
    [Fact]
    public async Task Purge_NotSentAsJson_IsRefusedAndDeletesNothing()
    {
        using var app = new TestApp(startUtc: Now);
        await SaveAsync(app, Session("Old", 400));
        using var admin = await app.SignedInAdminAsync();

        var form = await admin.PostAsync(PurgePath, new FormUrlEncodedContent([new("olderThanDays", "30")]));
        var text = await admin.PostAsync(PurgePath, new StringContent("{\"olderThanDays\":30}", System.Text.Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, form.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);
        Assert.Equal(["Old"], await RemainingPcNamesAsync(app));
    }

    [Fact]
    public async Task Preview_IsNotCached()
    {
        using var app = new TestApp(startUtc: Now);
        using var admin = await app.SignedInAdminAsync();

        var response = await admin.GetAsync($"{PreviewPath}?olderThanDays=30");

        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Requests_OnThePcPort_AreNotFound()
    {
        using var app = new TestApp(startUtc: Now);
        using var pc = app.CreatePcClient();

        Assert.Equal(HttpStatusCode.NotFound, (await pc.GetAsync($"{PreviewPath}?olderThanDays=30")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pc.PostAsJsonAsync(PurgePath, new { olderThanDays = 30 })).StatusCode);
    }
}
