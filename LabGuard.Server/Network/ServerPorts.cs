using System.Globalization;

namespace LabGuard.Server.Network;

// AdminPort serves the admin web UI on loopback only; ClientPort serves lab PCs on the LAN.
public sealed record ServerPorts(int AdminPort, int ClientPort)
{
    private const string AdminPortKey = "Server:Port";
    private const string ClientPortKey = "Server:ClientPort";

    public static ServerPorts FromConfiguration(IConfiguration configuration)
    {
        var ports = new ServerPorts(
            ReadPort(configuration, AdminPortKey, 5000),
            ReadPort(configuration, ClientPortKey, 5001));

        if (ports.AdminPort == ports.ClientPort)
        {
            throw new InvalidOperationException(
                $"{AdminPortKey} and {ClientPortKey} must be different; the admin port must stay separate from the PC port.");
        }

        return ports;
    }

    // Port 0 would make the OS pick a random port, which no guard rule could recognize.
    private static int ReadPort(IConfiguration configuration, string key, int defaultPort)
    {
        var text = configuration[key];
        if (text is null)
        {
            return defaultPort;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"{key} must be a whole number from 1 to 65535, but was '{text}'.");
        }

        return port;
    }
}
