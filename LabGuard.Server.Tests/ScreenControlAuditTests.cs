using LabGuard.Server.Data;
using LabGuard.Server.Screen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

public class ScreenControlAuditTests
{
    private static async Task<int> InsertRowAsync(TestApp app, DateTimeOffset? endedAt)
    {
        var scopes = app.Services.GetRequiredService<IServiceScopeFactory>();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = new ScreenControlEvent
        {
            AdminUserName = "tester",
            PcName = "PC-01",
            StartedUtc = app.Time.GetUtcNow().AddMinutes(-10),
            EndedUtc = endedAt,
        };
        db.ScreenControlEvents.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static Task<ScreenControlEvent> StoredAsync(TestApp app, int id) =>
        app.WithDbAsync(db => db.ScreenControlEvents.AsNoTracking().SingleAsync(e => e.Id == id));

    [Fact]
    public async Task StartAsync_RecordsTheAdminPcAndStart_WithoutAnEnd()
    {
        using var app = new TestApp();
        var audit = app.Services.GetRequiredService<ScreenControlAudit>();
        var startedAt = app.Time.GetUtcNow();

        var id = await audit.StartAsync("tester", "Lab-Desk-3", startedAt, CancellationToken.None);

        var stored = await StoredAsync(app, id);
        Assert.Equal("tester", stored.AdminUserName);
        Assert.Equal("Lab-Desk-3", stored.PcName);
        Assert.Equal(startedAt.ToUnixTimeMilliseconds(), stored.StartedUtc.ToUnixTimeMilliseconds());
        Assert.Null(stored.EndedUtc);
    }

    [Fact]
    public async Task EndAsync_SetsTheEnd_OfTheRow()
    {
        using var app = new TestApp();
        var audit = app.Services.GetRequiredService<ScreenControlAudit>();
        var id = await InsertRowAsync(app, endedAt: null);
        var endedAt = app.Time.GetUtcNow();

        await audit.EndAsync(id, endedAt);

        var stored = await StoredAsync(app, id);
        Assert.Equal(endedAt.ToUnixTimeMilliseconds(), stored.EndedUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task EndAsync_DoesNotOverwriteAnEarlierEnd()
    {
        using var app = new TestApp();
        var audit = app.Services.GetRequiredService<ScreenControlAudit>();
        var earlier = app.Time.GetUtcNow().AddMinutes(-1);
        var id = await InsertRowAsync(app, endedAt: earlier);

        await audit.EndAsync(id, app.Time.GetUtcNow());

        var stored = await StoredAsync(app, id);
        Assert.Equal(earlier.ToUnixTimeMilliseconds(), stored.EndedUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task EndAsync_ForAMissingRow_DoesNotThrow()
    {
        using var app = new TestApp();
        var audit = app.Services.GetRequiredService<ScreenControlAudit>();

        await audit.EndAsync(987654, app.Time.GetUtcNow());

        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Contains("Couldn't record"));
    }

    [Fact]
    public async Task EndAsync_WhenTheTableIsGone_LogsOnlyTheExceptionTypeAndDoesNotThrow()
    {
        using var app = new TestApp();
        var audit = app.Services.GetRequiredService<ScreenControlAudit>();
        var id = await InsertRowAsync(app, endedAt: null);
        await app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE \"ScreenControlEvents\""));

        await audit.EndAsync(id, app.Time.GetUtcNow());

        var logs = string.Join("\n", app.Logs.Entries);
        Assert.Contains("Couldn't record the end of screen control", logs);
    }

    [Fact]
    public async Task CloseOrphanedAsync_EndsOnlyTheRowsStillOpen_AtTheStartupTime()
    {
        using var app = new TestApp();
        var audit = app.Services.GetRequiredService<ScreenControlAudit>();
        var earlier = app.Time.GetUtcNow().AddMinutes(-5);
        var openId = await InsertRowAsync(app, endedAt: null);
        var closedId = await InsertRowAsync(app, endedAt: earlier);

        await audit.CloseOrphanedAsync(app.Time.GetUtcNow(), CancellationToken.None);

        var open = await StoredAsync(app, openId);
        var closed = await StoredAsync(app, closedId);
        // The database keeps milliseconds, so the times are compared at that precision.
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), open.EndedUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(earlier.ToUnixTimeMilliseconds(), closed.EndedUtc!.Value.ToUnixTimeMilliseconds());
    }
}
