using LabGuard.Server.Data;
using LabGuard.Server.Logs;

namespace LabGuard.Server.Tests;

public class SessionLogFilterTests
{
    private static SessionLogListQuery Query(
        string? from = null,
        string? to = null,
        string? pc = null,
        string? user = null,
        string? page = null,
        string? pageSize = null) => new(from, to, pc, user, page, pageSize);

    private static void AssertRejected(SessionLogListQuery query, string expectedError)
    {
        Assert.False(SessionLogFilter.TryParse(query, out var filter, out var error));
        Assert.Null(filter);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void TryParse_NoValues_UsesTheDefaults()
    {
        Assert.True(SessionLogFilter.TryParse(Query(), out var filter, out var error));

        Assert.Null(error);
        Assert.Equal(new SessionLogFilter(null, null, null, null, 1, 10), filter);
    }

    [Fact]
    public void TryParse_BlankValues_CountAsNotGiven()
    {
        Assert.True(SessionLogFilter.TryParse(Query(from: "  ", to: "", pc: " ", user: "", page: " ", pageSize: ""), out var filter, out _));

        Assert.Equal(new SessionLogFilter(null, null, null, null, 1, 10), filter);
    }

    [Fact]
    public void TryParse_ValidValues_AreParsedAndTrimmed()
    {
        var parsed = SessionLogFilter.TryParse(
            Query(from: "2026-11-01", to: " 2026-11-09 ", pc: "  Lab-  ", user: " alice ", page: " 3 ", pageSize: "50"),
            out var filter,
            out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(new SessionLogFilter(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 9), "Lab-", "alice", 3, 50), filter);
    }

    [Fact]
    public void TryParse_FromEqualToTo_IsAccepted()
    {
        Assert.True(SessionLogFilter.TryParse(Query(from: "2026-11-09", to: "2026-11-09"), out _, out _));
    }

    [Theory]
    [InlineData("2026-1-5")]
    [InlineData("09/11/2026")]
    [InlineData("2026-11-09T10:00")]
    [InlineData("2026-02-30")]
    [InlineData("yesterday")]
    public void TryParse_BadFromDate_IsRefused(string from)
    {
        AssertRejected(Query(from: from), "From must be a date in the form yyyy-MM-dd.");
    }

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("tomorrow")]
    public void TryParse_BadToDate_IsRefused(string to)
    {
        AssertRejected(Query(to: to), "To must be a date in the form yyyy-MM-dd.");
    }

    [Fact]
    public void TryParse_FromAfterTo_IsRefused()
    {
        AssertRejected(Query(from: "2026-11-10", to: "2026-11-09"), "The From date can't be after the To date.");
    }

    [Fact]
    public void TryParse_PcFilterOfExactlyTheLimit_IsAccepted()
    {
        var pc = new string('p', SessionLogFilter.MaxTextLength);

        Assert.True(SessionLogFilter.TryParse(Query(pc: pc), out var filter, out _));
        Assert.Equal(pc, filter.PcName);
    }

    [Fact]
    public void TryParse_PcFilterOverTheLimit_IsRefused()
    {
        AssertRejected(Query(pc: new string('p', SessionLogFilter.MaxTextLength + 1)), "The PC filter can be at most 64 characters.");
    }

    [Fact]
    public void TryParse_UserFilterOverTheLimit_IsRefused()
    {
        AssertRejected(Query(user: new string('u', SessionLogFilter.MaxTextLength + 1)), "The user filter can be at most 64 characters.");
    }

    [Fact]
    public void TryParse_LengthIsCheckedAfterTrimming()
    {
        var padded = "  " + new string('u', SessionLogFilter.MaxTextLength) + "  ";

        Assert.True(SessionLogFilter.TryParse(Query(user: padded), out var filter, out _));
        Assert.Equal(new string('u', SessionLogFilter.MaxTextLength), filter.UserName);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+2")]
    [InlineData("abc")]
    [InlineData("1.5")]
    public void TryParse_BadPage_IsRefused(string page)
    {
        AssertRejected(Query(page: page), "Page must be a whole number, 1 or more.");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("-5")]
    [InlineData("ten")]
    public void TryParse_PageSizeOutOfRange_IsRefused(string pageSize)
    {
        AssertRejected(Query(pageSize: pageSize), "Page size must be a whole number from 1 to 100.");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("100")]
    public void TryParse_PageSizeAtTheBounds_IsAccepted(string pageSize)
    {
        Assert.True(SessionLogFilter.TryParse(Query(pageSize: pageSize), out _, out _));
    }

    [Fact]
    public void NewestFirst_OrdersRunningFirstThenNewestStart_ThenNewestId()
    {
        // The ordering is applied to an in-memory list here. The SQL ordering is covered by the endpoint tests.
        var sessions = new[]
        {
            new PcSession { Id = 1, StartedAtUtc = new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero), EndedAtUtc = new DateTimeOffset(2026, 11, 9, 9, 5, 0, TimeSpan.Zero) },
            new PcSession { Id = 2, StartedAtUtc = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero) },
            new PcSession { Id = 3, StartedAtUtc = new DateTimeOffset(2026, 11, 9, 11, 0, 0, TimeSpan.Zero), EndedAtUtc = new DateTimeOffset(2026, 11, 9, 11, 1, 0, TimeSpan.Zero) },
            new PcSession { Id = 4, StartedAtUtc = new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero), EndedAtUtc = new DateTimeOffset(2026, 11, 9, 9, 5, 0, TimeSpan.Zero) },
        };

        var ordered = SessionLogFilter.NewestFirst(sessions.AsQueryable()).Select(s => s.Id).ToArray();

        Assert.Equal(new[] { 2, 3, 4, 1 }, ordered);
    }
}
