using System.Net;
using System.Net.Sockets;

namespace LabGuard.Server.Network;

// One network adapter, reduced to what the address rules need.
public sealed record LanAdapter(
    string Name,
    string Description,
    bool IsUp,
    bool IsLoopback,
    bool HasIpv4Gateway,
    IReadOnlyList<IPAddress> Ipv4Addresses);

public sealed record LanAddresses(string? Primary, IReadOnlyList<string> Others);

// Picks the address lab PCs most likely reach this computer on. Virtual adapters (Hyper-V, VirtualBox and similar)
// are never preferred for the primary address, but their addresses are still listed under the others.
public static class LanAddressSelector
{
    private static readonly string[] VirtualMarkers =
    [
        "Hyper-V",
        "vEthernet",
        "VirtualBox",
        "VMware",
        "WSL",
        "Loopback",
        "TAP",
        "Bluetooth",
    ];

    public static LanAddresses Select(IEnumerable<LanAdapter> adapters)
    {
        var candidates = adapters
            .Where(adapter => adapter.IsUp && !adapter.IsLoopback)
            .SelectMany(adapter => adapter.Ipv4Addresses
                .Where(IsLanAddress)
                .Select(address => new Candidate(address.ToString(), adapter)))
            .ToList();

        var primary = candidates.FirstOrDefault(c => c.Adapter.HasIpv4Gateway && !IsVirtual(c.Adapter))
            ?? candidates.FirstOrDefault(c => !IsVirtual(c.Adapter));

        var others = candidates
            .Select(c => c.Address)
            .Where(address => address != primary?.Address)
            .Distinct()
            .ToList();

        return new LanAddresses(primary?.Address, others);
    }

    private sealed record Candidate(string Address, LanAdapter Adapter);

    // IPv4 only, and not a loopback or self-assigned (169.254.x.x) address.
    private static bool IsLanAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return !(bytes[0] == 169 && bytes[1] == 254);
    }

    private static bool IsVirtual(LanAdapter adapter) =>
        VirtualMarkers.Any(marker =>
            adapter.Name.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || adapter.Description.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
