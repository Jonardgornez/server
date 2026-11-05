using System.Net;
using Microsoft.AspNetCore.Http.Features;

namespace LabGuard.Server.Network;

// Keeps the two network paths apart and fails closed. It decides by the connection itself (local port and
// addresses), never by the Host header, which the sender controls. A connection that is neither the PC
// channel nor a loopback connection to the admin port gets an empty 404, whatever the configuration.
public sealed class ChannelGuard(RequestDelegate next, ServerPorts ports, ILogger<ChannelGuard> logger)
{
    public const int MaxClientBodyBytes = 4 * 1024;
    private const string ClientPathPrefix = "/api/client/";

    private static readonly HashSet<string> AdminHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "127.0.0.1",
        "[::1]",
    };

    public Task InvokeAsync(HttpContext context)
    {
        var isClientPath = context.Request.Path.Value?.StartsWith(ClientPathPrefix, StringComparison.OrdinalIgnoreCase) == true;
        var connection = context.Connection;

        if (connection.LocalPort == ports.ClientPort)
        {
            // IsHttps fails closed if a setting ever adds a plain listener on this port.
            return isClientPath && context.Request.IsHttps
                ? InvokeClientAsync(context)
                : Reject(context, StatusCodes.Status404NotFound);
        }

        var isLoopbackAdminConnection = connection.LocalPort == ports.AdminPort
            && IsLoopback(connection.RemoteIpAddress)
            && IsLoopback(connection.LocalIpAddress);
        if (!isLoopbackAdminConnection)
        {
            return Reject(context, StatusCodes.Status404NotFound);
        }

        // Host check on the admin port keeps DNS-rebinding protection now that AllowedHosts is open.
        if (!AdminHosts.Contains(context.Request.Host.Host))
        {
            return Reject(context, StatusCodes.Status400BadRequest);
        }

        return isClientPath ? Reject(context, StatusCodes.Status404NotFound) : next(context);
    }

    private static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        return IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }

    private async Task InvokeClientAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaxClientBodyBytes)
        {
            await Reject(context, StatusCodes.Status413PayloadTooLarge);
            return;
        }

        var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false })
        {
            bodyLimit.MaxRequestBodySize = MaxClientBodyBytes;
        }

        // Handled here so the developer exception page never echoes a PC's request headers (its token) on the LAN port.
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The PC went away mid-request; nothing to report or send.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled error on the PC channel for {Path}.", context.Request.Path);
            if (context.Response.HasStarted)
            {
                context.Abort();
            }
            else
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        }
    }

    private static Task Reject(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        return Task.CompletedTask;
    }
}
