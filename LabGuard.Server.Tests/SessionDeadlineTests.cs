using System.Globalization;
using System.Security.Claims;
using LabGuard.Server.Auth;
using Microsoft.AspNetCore.Authentication;

namespace LabGuard.Server.Tests;

public class SessionDeadlineTests
{
    private static readonly DateTimeOffset Issued = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    private static ClaimsPrincipal Principal(string? idleMinutes) =>
        new(new ClaimsIdentity(
            idleMinutes is null ? [] : [new Claim(AuthServiceExtensions.IdleTimeoutMinutesClaim, idleMinutes)],
            "test"));

    private static AuthenticationProperties Properties(DateTimeOffset? issued, DateTimeOffset? expires = null) =>
        new() { IssuedUtc = issued, ExpiresUtc = expires };

    [Fact]
    public void SessionDeadline_IsTheIdleLimitCountedFromTheLastRenewal()
    {
        var deadline = AuthServiceExtensions.SessionDeadlineUtc(Principal("30"), Properties(Issued));

        Assert.Equal(Issued.AddMinutes(30), deadline);
    }

    [Fact]
    public void SessionDeadline_TheCookieExpiryWinsWhenItIsSooner()
    {
        var cookieExpiry = Issued.AddMinutes(10);

        var deadline = AuthServiceExtensions.SessionDeadlineUtc(Principal("30"), Properties(Issued, cookieExpiry));

        Assert.Equal(cookieExpiry, deadline);
    }

    [Fact]
    public void SessionDeadline_IsNullWithoutASavedLimitOrAnIssueTime()
    {
        Assert.Null(AuthServiceExtensions.SessionDeadlineUtc(Principal(null), Properties(Issued)));
        Assert.Null(AuthServiceExtensions.SessionDeadlineUtc(Principal("not-a-number"), Properties(Issued)));
        Assert.Null(AuthServiceExtensions.SessionDeadlineUtc(Principal("30"), Properties(null)));
        Assert.Null(AuthServiceExtensions.SessionDeadlineUtc(null, Properties(Issued)));
        Assert.Null(AuthServiceExtensions.SessionDeadlineUtc(Principal("30"), null));
    }

    [Fact]
    public void SessionDeadline_UsesTheInvariantNumberFormat()
    {
        var principal = Principal(15.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(Issued.AddMinutes(15), AuthServiceExtensions.SessionDeadlineUtc(principal, Properties(Issued)));
    }
}
