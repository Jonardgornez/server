using System.Net;
using LabGuard.Server.Network;

namespace LabGuard.Server.Screen;

// The machine's addresses, read at most once per cache lifetime. A failed read is not kept, so the next check reads again.
public sealed class LocalAddressCache(ILocalAddressSource source, TimeProvider clock)
{
    private readonly Lock _sync = new();
    private IReadOnlyList<IPAddress>? _addresses;
    private DateTimeOffset _readAt;

    // Null when the addresses can't be read. The caller fails closed on null.
    public IReadOnlyList<IPAddress>? ReadOrNull()
    {
        lock (_sync)
        {
            var now = clock.GetUtcNow();
            if (_addresses is not null && now - _readAt < ScreenProtocol.LocalAddressCacheLifetime)
            {
                return _addresses;
            }

            _addresses = source.ReadOrNull();
            _readAt = now;
            return _addresses;
        }
    }
}
