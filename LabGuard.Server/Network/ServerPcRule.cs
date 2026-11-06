using System.Net;

namespace LabGuard.Server.Network;

// Whether a PC is the computer this server runs on: its stored address is a loopback address or one of this machine's addresses.
public static class ServerPcRule
{
    public static bool IsServerPc(string? storedAddress, IEnumerable<IPAddress> machineAddresses)
    {
        if (!IPAddress.TryParse(storedAddress, out var address))
        {
            return false;
        }

        var pcAddress = Normalize(address);
        return IPAddress.IsLoopback(pcAddress)
            || machineAddresses.Any(machineAddress => Normalize(machineAddress).Equals(pcAddress));
    }

    // An IPv4-mapped IPv6 address becomes plain IPv4, and an IPv6 scope id is dropped, so the same address compares equal.
    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : new IPAddress(address.GetAddressBytes());
}
