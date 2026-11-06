using System.Net.WebSockets;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Pcs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Screen;

// The administrator's live view of one PC, over a WebSocket on the admin port. The PC streams only while a viewer is open.
public static class ScreenViewEndpoints
{
    public static IServiceCollection AddScreenViewing(this IServiceCollection services)
    {
        services.AddSingleton<ScreenStreamHub>();
        services.AddSingleton<ScreenViewAudit>();
        services.AddSingleton<ScreenViewMonitor>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ScreenViewMonitor>());
        return services;
    }

    public static IEndpointRouteBuilder MapScreenViewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/pcs/{id:int}/screen/live", LiveAsync)
            .WithMetadata(new NoSessionRenewalAttribute())
            .AddEndpointFilter<PcStorageErrorFilter>();
        return app;
    }

    // Every refusal is answered before the WebSocket is accepted, so a refused request never upgrades.
    private static async Task<IResult> LiveAsync(
        int id,
        string? mode,
        HttpContext http,
        AppDbContext db,
        TimeProvider clock,
        ScreenStreamHub hub,
        ScreenViewAudit audit)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            return AuthEndpoints.BadRequest("This address needs a WebSocket connection.");
        }

        // Stops another site from opening this view through the administrator's cookie (cross-site WebSocket hijacking).
        if (!ScreenProtocol.IsLoopbackOrigin(http.Request.Headers.Origin.ToString()))
        {
            return Results.Json(new ErrorResponse("The live view can only be opened from this server's admin page."), statusCode: StatusCodes.Status403Forbidden);
        }

        if (ScreenProtocol.ParseMode(mode) is not { } viewMode)
        {
            return AuthEndpoints.BadRequest("Mode must be notified or anonymous.");
        }

        var ct = http.RequestAborted;
        var now = clock.GetUtcNow();
        var pc = await db.Pcs
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new { p.Name, p.LastSeenUtc })
            .FirstOrDefaultAsync(ct);
        if (pc is null)
        {
            return PcEndpoints.PcNotFound();
        }

        if (!PcEndpoints.IsOnline(pc.LastSeenUtc, now))
        {
            return PcEndpoints.ConflictResponse("This PC is offline.");
        }

        var session = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (AuthServiceExtensions.SessionDeadlineUtc(session.Principal, session.Properties) is not { } sessionDeadline)
        {
            return AuthEndpoints.SignInRequired();
        }

        var auditId = await audit.StartAsync(http.User.Identity?.Name ?? "", pc.Name, viewMode, now, ct);
        WebSocket socket;
        try
        {
            socket = await http.WebSockets.AcceptWebSocketAsync();
        }
        catch
        {
            await audit.EndAsync(auditId, clock.GetUtcNow());
            throw;
        }

        var viewer = await hub.OpenAsync(id, viewMode, sessionDeadline, auditId, new ScreenSocket(socket, ScreenProtocol.SendTimeout));
        try
        {
            await ReceiveBrowserMessagesAsync(hub, viewer, ct);
        }
        finally
        {
            await hub.CloseAsync(viewer, WebSocketCloseStatus.NormalClosure, "The viewer closed.");
        }

        return Results.Empty;
    }

    // The browser's only message is "ack". Anything else is ignored, and an oversized message closes the socket.
    private static async Task ReceiveBrowserMessagesAsync(ScreenStreamHub hub, ScreenViewer viewer, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, viewer.Lifetime);
        while (await viewer.Browser.ReceiveAsync(ScreenProtocol.MaxBrowserMessageBytes, linked.Token) is { } message)
        {
            if (ScreenProtocol.IsBrowserAck(message.Type, message.Payload.Span))
            {
                await hub.RelayAckAsync(viewer);
            }
        }
    }
}
