using LabGuard.Server.Data;
using LabGuard.Server.Requests;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Tests;

public class LoginRequestLifecycleTests
{
    private static readonly DateTimeOffset Requested = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 30)]
    [InlineData(10, 20)]
    [InlineData(29.5, 1)]
    [InlineData(29.999, 1)]
    public void CooldownSecondsLeft_IsTheTimeLeftRoundedUp(double secondsElapsed, int expected)
    {
        var now = Requested.AddSeconds(secondsElapsed);

        Assert.Equal(expected, LoginRequestLifecycle.CooldownSecondsLeft(Requested, now));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    public void CooldownSecondsLeft_IsNullOnceTheCooldownHasPassed(double secondsElapsed)
    {
        Assert.Null(LoginRequestLifecycle.CooldownSecondsLeft(Requested, Requested.AddSeconds(secondsElapsed)));
    }

    [Fact]
    public void IsDecisionReported_IsTrueUntilTwoMinutesAfterTheDecision()
    {
        Assert.True(LoginRequestLifecycle.IsDecisionReported(Requested, Requested.AddSeconds(119)));
        Assert.False(LoginRequestLifecycle.IsDecisionReported(Requested, Requested.AddMinutes(2)));
        Assert.True(LoginRequestLifecycle.IsDecisionReported(Requested.AddSeconds(5), Requested));
    }

    [Theory]
    [InlineData(599, false)]
    [InlineData(600, true)]
    [InlineData(601, true)]
    public void IsExpired_IsTrueFromExactlyTenMinutesAfterTheRequest(int secondsOld, bool expected)
    {
        Assert.Equal(expected, LoginRequestLifecycle.IsExpired(Requested, Requested.AddSeconds(secondsOld)));
    }

    [Fact]
    public void ExpiredAt_IsTenMinutesAfterTheRequest()
    {
        Assert.Equal(Requested.AddMinutes(10), LoginRequestLifecycle.ExpiredAt(Requested));
    }

    [Theory]
    [InlineData(LoginRequestStatus.Pending, "pending")]
    [InlineData(LoginRequestStatus.Approved, "approved")]
    [InlineData(LoginRequestStatus.Denied, "denied")]
    [InlineData(LoginRequestStatus.Cancelled, "cancelled")]
    public void WireStatus_IsTheLowercaseStatusName(LoginRequestStatus status, string expected)
    {
        Assert.Equal(expected, LoginRequestLifecycle.WireStatus(status));
    }

    private static async Task<int> AddAsync(TestApp app, LoginRequest request) =>
        await app.WithDbAsync(async db =>
        {
            db.LoginRequests.Add(request);
            await db.SaveChangesAsync();
            return request.Id;
        });

    private static Task<LoginRequest> StoredAsync(TestApp app, int id) =>
        app.WithDbAsync(db => db.LoginRequests.AsNoTracking().SingleAsync(r => r.Id == id));

    [Fact]
    public async Task ApproveIfFresh_JustBeforeTenMinutes_ApprovesThePendingRequest()
    {
        using var app = new TestApp();
        var id = await AddAsync(app, new LoginRequest { PcName = "PC-01", RequestedAtUtc = app.Time.GetUtcNow(), Status = LoginRequestStatus.Pending });
        app.Time.Advance(TimeSpan.FromMilliseconds(599_500));

        var approved = await app.WithDbAsync(db => LoginRequestLifecycle.ApproveIfFreshAsync(db, id, 30, app.Time, CancellationToken.None));

        Assert.Equal(1, approved);
        var stored = await StoredAsync(app, id);
        Assert.Equal(LoginRequestStatus.Approved, stored.Status);
        Assert.Equal(30, stored.ApprovedMinutes);
    }

    [Fact]
    public async Task ApproveIfFresh_AtTenMinutes_ApprovesNothingAndLeavesItPending()
    {
        using var app = new TestApp();
        var id = await AddAsync(app, new LoginRequest { PcName = "PC-01", RequestedAtUtc = app.Time.GetUtcNow(), Status = LoginRequestStatus.Pending });
        app.Time.Advance(TimeSpan.FromMinutes(10));

        var approved = await app.WithDbAsync(db => LoginRequestLifecycle.ApproveIfFreshAsync(db, id, 30, app.Time, CancellationToken.None));

        Assert.Equal(0, approved);
        Assert.Equal(LoginRequestStatus.Pending, (await StoredAsync(app, id)).Status);
    }

    [Fact]
    public async Task ExpireStale_ExpiresOnlyStalePendingRequests_AndLeavesDecidedOnesAlone()
    {
        using var app = new TestApp();
        var now = app.Time.GetUtcNow();
        var stale = await AddAsync(app, new LoginRequest { PcName = "PC-01", RequestedAtUtc = now.AddMinutes(-11), Status = LoginRequestStatus.Pending });
        var fresh = await AddAsync(app, new LoginRequest { PcName = "PC-02", RequestedAtUtc = now.AddMinutes(-1), Status = LoginRequestStatus.Pending });
        var decidedAt = now.AddMinutes(-15);
        var denied = await AddAsync(app, new LoginRequest
        {
            PcName = "PC-03",
            RequestedAtUtc = now.AddMinutes(-20),
            Status = LoginRequestStatus.Denied,
            DecidedAtUtc = decidedAt,
        });

        var expired = await app.WithDbAsync(db => LoginRequestLifecycle.ExpireStaleAsync(db, now, CancellationToken.None));

        Assert.Equal(1, expired);
        var expiredRow = await StoredAsync(app, stale);
        Assert.Equal(LoginRequestStatus.Cancelled, expiredRow.Status);
        Assert.Equal(LoginRequestLifecycle.Expired, expiredRow.Reason);
        Assert.Equal(now.AddMinutes(-1).ToUnixTimeMilliseconds(), expiredRow.DecidedAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(LoginRequestStatus.Pending, (await StoredAsync(app, fresh)).Status);
        var deniedRow = await StoredAsync(app, denied);
        Assert.Equal(LoginRequestStatus.Denied, deniedRow.Status);
        Assert.Null(deniedRow.Reason);
        Assert.Equal(decidedAt.ToUnixTimeMilliseconds(), deniedRow.DecidedAtUtc!.Value.ToUnixTimeMilliseconds());
    }
}
