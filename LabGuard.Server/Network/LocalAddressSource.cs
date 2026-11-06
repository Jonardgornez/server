using System.Net;
using System.Net.NetworkInformation;

namespace LabGuard.Server.Network;

// The addresses this computer has. A small service so tests can replace it with fixed addresses.
public interface ILocalAddressSource
{
    IReadOnlyList<IPAddress> Read();
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
