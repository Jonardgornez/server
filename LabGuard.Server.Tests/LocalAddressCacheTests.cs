using System.Net;
using System.Net.NetworkInformation;
using LabGuard.Server.Network;
using LabGuard.Server.Screen;
using Microsoft.Extensions.Time.Testing;

namespace LabGuard.Server.Tests;

public class LocalAddressCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    // Counts the reads, and can be told to fail the way a read of the adapters does.
    private sealed class CountingAddressSource : ILocalAddressSource
    {
        public int Reads { get; private set; }

        public bool Fail { get; set; }

        public IReadOnlyList<IPAddress> Read()
        {
            Reads++;
            if (Fail)
            {
                throw new NetworkInformationException();
            }

            return [IPAddress.Parse("198.51.100.10")];
        }
    }

    [Fact]
    public void ReadOrNull_ReusesTheAddresses_WithinTheLifetime()
    {
        var source = new CountingAddressSource();
        var cache = new LocalAddressCache(source, new FakeTimeProvider(Start));

        var first = cache.ReadOrNull();
        var second = cache.ReadOrNull();

        Assert.Equal(1, source.Reads);
        Assert.Same(first, second);
    }

    [Fact]
    public void ReadOrNull_ReadsAgain_OnceTheLifetimeHasPassed()
    {
        var source = new CountingAddressSource();
        var clock = new FakeTimeProvider(Start);
        var cache = new LocalAddressCache(source, clock);
        cache.ReadOrNull();

        clock.Advance(ScreenProtocol.LocalAddressCacheLifetime - TimeSpan.FromMilliseconds(1));
        cache.ReadOrNull();
        Assert.Equal(1, source.Reads);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        cache.ReadOrNull();
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void ReadOrNull_AFailedRead_ReturnsNull_AndIsNotKept()
    {
        var source = new CountingAddressSource { Fail = true };
        var cache = new LocalAddressCache(source, new FakeTimeProvider(Start));

        Assert.Null(cache.ReadOrNull());
        source.Fail = false;

        Assert.NotNull(cache.ReadOrNull());
        Assert.Equal(2, source.Reads);
    }
}
