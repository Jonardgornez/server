using LabGuard.Server.Network;

namespace LabGuard.Server.Screen;

// The only origins the administrator page can open a live view from. Matching is exact, so a different port, scheme or host is refused.
// The admin UI is built to admin/dist and served by `vite preview` on 4173 with the same /api proxy, so 4173 is allowed in every environment.
// The Vite dev server (5173) is allowed only in Development.
public sealed class ScreenOrigins
{
    private const string DevServerOrigin = "http://localhost:5173";
    private const string PreviewOrigin = "http://localhost:4173";

    private readonly HashSet<string> _allowed;

    public ScreenOrigins(ServerPorts ports, bool isDevelopment)
    {
        _allowed = new(StringComparer.Ordinal)
        {
            $"http://localhost:{ports.AdminPort}",
            $"http://127.0.0.1:{ports.AdminPort}",
            $"http://[::1]:{ports.AdminPort}",
            PreviewOrigin,
        };
        if (isDevelopment)
        {
            _allowed.Add(DevServerOrigin);
        }
    }

    public bool IsAllowed(string? origin) => !string.IsNullOrEmpty(origin) && _allowed.Contains(origin);
}
