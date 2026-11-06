using LabGuard.Server.Data;
using LabGuard.Server.Screen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

public class ScreenViewAuditTests
{
    private static async Task<int> InsertRowAsync(TestApp app, DateTimeOffset? endedAt)
    {
        var scopes = app.Services.GetRequiredService<IServiceScopeFactory>();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = new ScreenViewEvent
        {
            AdminUsername = "tester",
            PcName = "PC-01",
            Mode = "notified",
            StartedAtUtc = app.Time.GetUtcNow().AddMinutes(-10),
            EndedAtUtc = endedAt,
        };
        db.ScreenViewEvents.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    [Fact]
    public async Task CloseOrphanedAsync_EndsOnlyTheRowsStillOpen_AtTheStartupTime()
    {
        using var app = new TestApp();
        var audit = app.Services.GetRequiredService<ScreenViewAudit>();
        var earlier = app.Time.GetUtcNow().AddMinutes(-5);
        var openId = await InsertRowAsync(app, endedAt: null);
        var closedId = await InsertRowAsync(app, endedAt: earlier);

        await audit.CloseOrphanedAsync(app.Time.GetUtcNow(), CancellationToken.None);

        var open = await app.WithDbAsync(db => db.ScreenViewEvents.AsNoTracking().SingleAsync(e => e.Id == openId));
        var closed = await app.WithDbAsync(db => db.ScreenViewEvents.AsNoTracking().SingleAsync(e => e.Id == closedId));
        // The database keeps milliseconds, so the times are compared at that precision.
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), open.EndedAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(earlier.ToUnixTimeMilliseconds(), closed.EndedAtUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task EndAsync_ForAMissingRow_DoesNotThrow()
    {
        using var app = new TestApp();
        var audit = app.Services.GetRequiredService<ScreenViewAudit>();

        await audit.EndAsync(987654, app.Time.GetUtcNow());

        Assert.Empty(app.Logs.Entries.Where(entry => entry.Contains("Couldn't record")));
    }

    [Fact]
    public async Task EndAsync_DoesNotOverwriteAnEarlierEnd()
    {
        using var app = new TestApp();
        var audit = app.Services.GetRequiredService<ScreenViewAudit>();
        var earlier = app.Time.GetUtcNow().AddMinutes(-1);
        var id = await InsertRowAsync(app, endedAt: earlier);

        await audit.EndAsync(id, app.Time.GetUtcNow());

        var stored = await app.WithDbAsync(db => db.ScreenViewEvents.AsNoTracking().SingleAsync(e => e.Id == id));
        Assert.Equal(earlier.ToUnixTimeMilliseconds(), stored.EndedAtUtc!.Value.ToUnixTimeMilliseconds());
    }
}
