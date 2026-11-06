using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LabGuard.Server.Network;

public record ServerInfoResponse(string? PrimaryAddress, IReadOnlyList<string> OtherAddresses, int ClientPort, int AdminPort);

public static class ServerInfoEndpoints
{
    public static IEndpointRouteBuilder MapServerInfoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/server-info");
        group.MapGet("/", GetAsync);
        return app;
    }

    // Read on every call, so the addresses follow the network as it changes.
    private static IResult GetAsync(ServerPorts ports)
    {
        var addresses = LanAddressSelector.Select(ReadAdapters());
        return Results.Ok(new ServerInfoResponse(addresses.Primary, addresses.Others, ports.ClientPort, ports.AdminPort));
    }

    private static IEnumerable<LanAdapter> ReadAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces().Select(ToLanAdapter);

    private static LanAdapter ToLanAdapter(NetworkInterface adapter)
    {
        var properties = adapter.GetIPProperties();
        return new LanAdapter(
            adapter.Name,
            adapter.Description,
            adapter.OperationalStatus == OperationalStatus.Up,
            adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback,
            properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)),
            properties.UnicastAddresses.Select(unicast => unicast.Address).ToList());
    }
}
