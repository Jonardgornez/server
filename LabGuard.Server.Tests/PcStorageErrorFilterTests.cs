using LabGuard.Server.Auth;
using LabGuard.Server.Pcs;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Tests;

public class PcStorageErrorFilterTests
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteConstraint = 19;

    private static async Task<object?> RunAsync(Exception? failure)
    {
        var filter = new PcStorageErrorFilter();
        var context = new DefaultEndpointFilterInvocationContext(new DefaultHttpContext());
        return await filter.InvokeAsync(context, _ => failure is null ? ValueTask.FromResult<object?>("ok") : throw failure);
    }

    private static void AssertBusyResult(object? result)
    {
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var body = Assert.IsType<ErrorResponse>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal("The server is busy. Try again in a moment.", body.Error);
    }

    [Fact]
    public async Task NoFailure_PassesTheHandlersResultThrough()
    {
        Assert.Equal("ok", await RunAsync(null));
    }

    [Theory]
    [InlineData(SqliteBusy)]
    [InlineData(SqliteLocked)]
    public async Task BusyOrLockedDatabase_Returns503(int code)
    {
        AssertBusyResult(await RunAsync(new SqliteException("busy", code)));
        AssertBusyResult(await RunAsync(new DbUpdateException("save failed", new SqliteException("busy", code))));
    }

    [Fact]
    public async Task OtherFailures_AreNotCaught()
    {
        await Assert.ThrowsAsync<SqliteException>(() => RunAsync(new SqliteException("constraint", SqliteConstraint)));
        await Assert.ThrowsAsync<DbUpdateException>(() => RunAsync(new DbUpdateException("save failed", new SqliteException("constraint", SqliteConstraint))));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => RunAsync(new DbUpdateConcurrencyException("gone")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(new InvalidOperationException("boom")));
    }
}
