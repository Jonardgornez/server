using System.Net.WebSockets;
using LabGuard.Server.Data;
using LabGuard.Server.Screen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

// The monitor ends a live view whose administrator session is no longer current, without waiting for a logout or password change to call it.
public class ScreenSessionMonitorTests
{
    private static async Task<WebSocket> OpenWaitingViewAsync(TestApp app, int pcId, string cookie)
    {
        var socket = await LiveViewClient.OpenAdminAsync(app, pcId, cookie);
        Assert.Equal("{\"state\":\"waiting\"}", await LiveViewClient.ReceiveStateAsync(socket));
        return socket;
    }

    [Fact]
    public async Task CheckOnce_LeavesAViewAlone_WhileItsSessionIsCurrent()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        using var browser = await OpenWaitingViewAsync(app, id, cookie);

        await app.Services.GetRequiredService<ScreenViewMonitor>().CheckOnceAsync(CancellationToken.None);

        Assert.NotNull(app.Services.GetRequiredService<ScreenStreamHub>().CurrentFor(id));
    }

    [Fact]
    public async Task CheckOnce_EndsAViewWhoseSessionVersionHasMovedOn_With4003()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        using var browser = await OpenWaitingViewAsync(app, id, cookie);
        await app.WithDbAsync(db => db.AdminAccounts.ExecuteUpdateAsync(setters => setters.SetProperty(a => a.SessionVersion, 99)));

        await app.Services.GetRequiredService<ScreenViewMonitor>().CheckOnceAsync(CancellationToken.None);

        var close = await LiveViewClient.ReceiveAsync(browser);
        Assert.Equal((WebSocketCloseStatus)4003, close.CloseStatus);
        Assert.Equal("The administrator session ended.", close.CloseReason);
        await WaitForOpenRowsAsync(app);
    }

    [Fact]
    public async Task CheckOnce_EndsAViewWhoseAdministratorNoLongerExists_With4003()
    {
        using var app = new TestApp();
        var (id, _) = await LiveViewClient.AddPcAsync(app);
        var cookie = await LiveViewClient.SignInCookieAsync(app);
        using var browser = await OpenWaitingViewAsync(app, id, cookie);
        await app.WithDbAsync(db => db.AdminAccounts.ExecuteDeleteAsync());

        await app.Services.GetRequiredService<ScreenViewMonitor>().CheckOnceAsync(CancellationToken.None);

        var close = await LiveViewClient.ReceiveAsync(browser);
        Assert.Equal((WebSocketCloseStatus)4003, close.CloseStatus);
        await WaitForOpenRowsAsync(app);
    }

    // The view's end is written after its close, so a test waits for the row before the host is torn down.
    private static async Task WaitForOpenRowsAsync(TestApp app)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await app.WithDbAsync(db => db.ScreenViewEvents.CountAsync(e => e.EndedAtUtc == null)) == 0)
            {
                return;
            }

            await Task.Delay(100);
        }
    }
}
