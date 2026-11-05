using LabGuard.Server.Auth;
using LabGuard.Server.Clients;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Tests;

public class ClientStorageErrorFilterTests
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteConstraint = 19;
    private const int SqliteBusySnapshot = 517;

    private static async Task<object?> RunAsync(Exception? failure)
    {
        var filter = new ClientStorageErrorFilter();
        var context = new DefaultEndpointFilterInvocationContext(new DefaultHttpContext());
        return await filter.InvokeAsync(context, _ => failure is null ? ValueTask.FromResult<object?>("ok") : throw failure);
    }

    private static void AssertResult(object? result, int status, string message)
    {
        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var body = Assert.IsType<ErrorResponse>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(message, body.Error);
    }

    [Fact]
    public async Task NoFailure_PassesTheHandlersResultThrough()
    {
        Assert.Equal("ok", await RunAsync(null));
    }

    [Fact]
    public async Task ConcurrencyFailure_Returns401NotRegistered()
    {
        AssertResult(await RunAsync(new DbUpdateConcurrencyException("gone")), 401, "This PC isn't registered.");
    }

    [Theory]
    [InlineData(SqliteBusy)]
    [InlineData(SqliteLocked)]
    [InlineData(SqliteBusySnapshot)]
    public async Task BusyOrLockedDatabase_Returns503(int code)
    {
        AssertResult(await RunAsync(new SqliteException("busy", code)), 503, "The server is busy. Try again in a moment.");
        AssertResult(
            await RunAsync(new DbUpdateException("save failed", new SqliteException("busy", code))),
            503,
            "The server is busy. Try again in a moment.");
    }

    [Fact]
    public async Task GateTimeout_Returns503()
    {
        AssertResult(await RunAsync(new ServerBusyException()), 503, "The server is busy. Try again in a moment.");
    }

    [Fact]
    public async Task OtherFailures_AreNotCaught()
    {
        await Assert.ThrowsAsync<SqliteException>(() => RunAsync(new SqliteException("constraint", SqliteConstraint)));
        await Assert.ThrowsAsync<DbUpdateException>(() => RunAsync(new DbUpdateException("save failed", new SqliteException("constraint", SqliteConstraint))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(new InvalidOperationException("boom")));
    }
}
