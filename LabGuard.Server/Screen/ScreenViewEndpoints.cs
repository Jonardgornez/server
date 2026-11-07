using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Network;
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
        services.AddSingleton<ScreenControlAudit>();
        services.AddSingleton<LocalAddressCache>();
        services.AddSingleton(provider => new ScreenOrigins(
            provider.GetRequiredService<ServerPorts>(),
            provider.GetRequiredService<IConfiguration>()));
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
        ScreenViewAudit audit,
        ScreenOrigins origins,
        LocalAddressCache localAddresses,
        ILoggerFactory loggerFactory)
    {
        // Read before the session is checked. A logout or password change after this point refuses the open.
        var epoch = hub.Epoch;
        var logger = loggerFactory.CreateLogger(nameof(ScreenViewEndpoints));

        if (!http.WebSockets.IsWebSocketRequest)
        {
            return AuthEndpoints.BadRequest("This address needs a WebSocket connection.");
        }

        // Stops another site from opening this view through the administrator's cookie (cross-site WebSocket hijacking).
        if (!origins.IsAllowed(http.Request.Headers.Origin.ToString()))
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
        if (AuthServiceExtensions.SessionDeadlineUtc(session.Principal, session.Properties) is not { } sessionDeadline
            || session.Principal is not { } principal
            || !TryReadSessionIdentity(principal, out var adminId, out var sessionVersion))
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

        var browser = new ScreenSocket(socket, ScreenProtocol.SendTimeout);
        try
        {
            var owner = new ScreenViewOwner(adminId, sessionVersion, http.User.Identity?.Name ?? "");
            var viewer = await hub.OpenAsync(id, viewMode, sessionDeadline, auditId, browser, epoch, owner, pc.Name);
            if (viewer is null)
            {
                await browser.CloseAsync(ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);
                return Results.Empty;
            }

            try
            {
                // The middleware checked the session before the epoch was read, so a password change may have landed in between.
                // This check runs after the viewer is published: a change after it finds the viewer and closes it.
                if (!await IsSessionCurrentAsync(db, adminId, sessionVersion, ct))
                {
                    await hub.CloseAsync(viewer, ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);
                }

                await ReceiveBrowserMessagesAsync(hub, viewer, db, localAddresses, logger, ct);
            }
            finally
            {
                await hub.CloseAsync(viewer, WebSocketCloseStatus.NormalClosure, "The viewer closed.");
                viewer.Dispose();
            }
        }
        finally
        {
            browser.Dispose();
        }

        return Results.Empty;
    }

    // True while the administrator's account still has the session version the cookie carries.
    public static async Task<bool> IsSessionCurrentAsync(AppDbContext db, int adminId, int sessionVersion, CancellationToken cancellationToken) =>
        await db.AdminAccounts
            .AsNoTracking()
            .Where(a => a.Id == adminId)
            .Select(a => (int?)a.SessionVersion)
            .FirstOrDefaultAsync(cancellationToken) == sessionVersion;

    private static bool TryReadSessionIdentity(ClaimsPrincipal principal, out int adminId, out int sessionVersion)
    {
        var hasAdminId = int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture, out adminId);
        var hasVersion = int.TryParse(principal.FindFirstValue(AuthServiceExtensions.SessionVersionClaim), CultureInfo.InvariantCulture, out sessionVersion);
        return hasAdminId && hasVersion;
    }

    // The browser sends "ack", control on or off, and input. Anything else is ignored, and an oversized message closes the socket.
    // Each message is handled on its own: a failure in one is logged by type and the view stays open.
    private static async Task ReceiveBrowserMessagesAsync(
        ScreenStreamHub hub,
        ScreenViewer viewer,
        AppDbContext db,
        LocalAddressCache localAddresses,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var linked = viewer.LinkLifetime(cancellationToken);
        if (linked is null)
        {
            return;
        }

        while (await viewer.Browser.ReceiveAsync(ScreenProtocol.BrowserLimits, linked.Token) is { } message)
        {
            try
            {
                await HandleBrowserMessageAsync(hub, viewer, db, localAddresses, logger, message, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Couldn't handle a live view message: {ExceptionType}.", exception.GetType().Name);
            }
        }
    }

    private static async Task HandleBrowserMessageAsync(
        ScreenStreamHub hub,
        ScreenViewer viewer,
        AppDbContext db,
        LocalAddressCache localAddresses,
        ILogger logger,
        ScreenMessage message,
        CancellationToken cancellationToken)
    {
        if (ScreenProtocol.IsBrowserAck(message.Type, message.Payload.Span))
        {
            await hub.RelayAckAsync(viewer);
            return;
        }

        var input = ScreenControlMessages.Parse(message.Type, message.Payload);
        switch (input.Kind)
        {
            case BrowserControlKind.ControlOn:
                // The cheap checks come first, so the database is only read for a request that may be answered.
                if (await hub.PrepareControlRequestAsync(viewer))
                {
                    await hub.RequestControlAsync(viewer, await IsServerPcOrRefusedAsync(db, viewer, localAddresses, logger, cancellationToken));
                }

                break;
            case BrowserControlKind.ControlOff:
                await hub.StopControlAsync(viewer);
                break;
            case BrowserControlKind.Move:
            case BrowserControlKind.Input:
                await hub.RelayInputAsync(viewer, input);
                break;
        }
    }

    // A failed check is refused as the server's own PC, so control is never granted on an error.
    private static async Task<bool> IsServerPcOrRefusedAsync(
        AppDbContext db,
        ScreenViewer viewer,
        LocalAddressCache localAddresses,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            return await IsServerPcAsync(db, viewer, localAddresses, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Couldn't check whether this PC is the server: {ExceptionType}.", exception.GetType().Name);
            return true;
        }
    }

    // Read when control is asked for. Both the stored address and the live address of the attached PC are checked against the
    // machine's addresses, so a PC is refused if either one is this machine. Fails closed: unreadable machine addresses, or a stored
    // address that is missing or not an IP address, count as the server's own PC.
    private static async Task<bool> IsServerPcAsync(
        AppDbContext db,
        ScreenViewer viewer,
        LocalAddressCache localAddresses,
        CancellationToken cancellationToken)
    {
        if (localAddresses.ReadOrNull() is not { } machineAddresses)
        {
            return true;
        }

        var stored = await db.Pcs
            .AsNoTracking()
            .Where(p => p.Id == viewer.PcId)
            .Select(p => new { p.IpAddress })
            .FirstOrDefaultAsync(cancellationToken);
        if (!IPAddress.TryParse(stored?.IpAddress, out var storedAddress))
        {
            return true;
        }

        if (ServerPcRule.IsServerPc(storedAddress.ToString(), machineAddresses))
        {
            return true;
        }

        return viewer.CurrentPc?.RemoteAddress is { } liveAddress
            && ServerPcRule.IsServerPc(liveAddress.ToString(), machineAddresses);
    }
}
