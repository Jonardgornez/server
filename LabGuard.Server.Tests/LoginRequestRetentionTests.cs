using LabGuard.Server.Data;
using LabGuard.Server.Requests;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Tests;

public class LoginRequestRetentionTests
{
    private static Task<int> AddAsync(TestApp app, LoginRequestStatus status, DateTimeOffset requestedAt, DateTimeOffset? decidedAt) =>
        app.WithDbAsync(async db =>
        {
            var request = new LoginRequest { PcName = "PC-01", RequestedAtUtc = requestedAt, Status = status, DecidedAtUtc = decidedAt };
            db.LoginRequests.Add(request);
            await db.SaveChangesAsync();
            return request.Id;
        });

    [Fact]
    public async Task DeleteDecided_RemovesDecidedRequestsOlderThanNinetyDays_AndKeepsTheRest()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow();
        var justTooOld = now - LoginRequestRetention.KeepFor - TimeSpan.FromSeconds(1);
        var exactlyKept = now - LoginRequestRetention.KeepFor;
        await AddAsync(app, LoginRequestStatus.Denied, justTooOld.AddMinutes(-5), justTooOld);
        await AddAsync(app, LoginRequestStatus.Approved, justTooOld.AddMinutes(-5), justTooOld);
        await AddAsync(app, LoginRequestStatus.Cancelled, justTooOld.AddMinutes(-5), justTooOld);
        var kept = await AddAsync(app, LoginRequestStatus.Denied, exactlyKept.AddMinutes(-1), exactlyKept);
        var recent = await AddAsync(app, LoginRequestStatus.Approved, now.AddDays(-1), now.AddDays(-1));
        var oldPending = await AddAsync(app, LoginRequestStatus.Pending, now - TimeSpan.FromDays(200), null);

        var removed = await app.WithDbAsync(db => LoginRequestRetention.DeleteDecidedOlderThanAsync(db, now, CancellationToken.None));

        Assert.Equal(3, removed);
        var remaining = await app.WithDbAsync(db => db.LoginRequests.AsNoTracking().Select(r => r.Id).ToListAsync());
        Assert.Equal(new[] { kept, recent, oldPending }.Order(), remaining.Order());
    }
}
