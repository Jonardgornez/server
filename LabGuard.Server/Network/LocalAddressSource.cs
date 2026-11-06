using System.Net;
using System.Net.NetworkInformation;

namespace LabGuard.Server.Network;

// The addresses this computer has. A small service so tests can replace it with fixed addresses.
public interface ILocalAddressSource
{
    // Throws NetworkInformationException when the adapters can't be read. Use ReadOrNull to handle that.
    IReadOnlyList<IPAddress> Read();
}

public static class LocalAddressSourceExtensions
{
    // Null when the addresses can't be read. An empty list means the machine really has none, so the two stay apart.
    public static IReadOnlyList<IPAddress>? ReadOrNull(this ILocalAddressSource source)
    {
        try
        {
            return source.Read();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}

// Every address on an adapter that is up, virtual adapters included.
public sealed class NetworkInterfaceAddressSource : ILocalAddressSource
{
    public IReadOnlyList<IPAddress> Read() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .ToList();
}
