using LabGuard.Server.Network;

namespace LabGuard.Server.Screen;

// The origins the administrator page can open a live view from. Matching is exact.
// Always allowed: the admin port on loopback. Everything else comes from Screen:AllowedOrigins. An entry is used only when it is
// exactly scheme://host[:port] with an http or https scheme and a loopback host. Anything else is ignored, so a bad setting
// can't open the view to the LAN.
public sealed class ScreenOrigins
{
    private const string ConfigurationKey = "Screen:AllowedOrigins";
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    private readonly HashSet<string> _allowed;

    public ScreenOrigins(ServerPorts ports, IConfiguration configuration)
    {
        _allowed = new(StringComparer.Ordinal)
        {
            $"http://localhost:{ports.AdminPort}",
            $"http://127.0.0.1:{ports.AdminPort}",
            $"http://[::1]:{ports.AdminPort}",
        };
        foreach (var configured in configuration.GetSection(ConfigurationKey).Get<string[]>() ?? [])
        {
            if (IsExactLoopbackOrigin(configured))
            {
                _allowed.Add(configured);
            }
        }
    }

    public bool IsAllowed(string? origin) => !string.IsNullOrEmpty(origin) && _allowed.Contains(origin);

    // Only the canonical form is accepted: no trailing slash, no path, no upper case, and no default port spelled out.
    public static bool IsExactLoopbackOrigin(string? text)
    {
        if (string.IsNullOrEmpty(text) || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https") || !LoopbackHosts.Contains(uri.Host, StringComparer.Ordinal))
        {
            return false;
        }

        return text == $"{uri.Scheme}://{uri.Authority}";
    }
}
