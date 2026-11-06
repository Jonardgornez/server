using System.Net;
using LabGuard.Server.Network;

namespace LabGuard.Server.Tests;

public class LocalAddressSourceTests
{
    [Fact]
    public void ReadOrNull_WhenTheAddressesCanBeRead_ReturnsThem()
    {
        var source = new FixedLocalAddressSource([IPAddress.Loopback]);

        Assert.Equal(IPAddress.Loopback, Assert.Single(source.ReadOrNull()!));
    }

    [Fact]
    public void ReadOrNull_WhenTheMachineHasNoAddresses_IsAnEmptyList_NotNull()
    {
        var source = new FixedLocalAddressSource([]);

        var addresses = source.ReadOrNull();

        Assert.NotNull(addresses);
        Assert.Empty(addresses);
    }

    [Fact]
    public void ReadOrNull_WhenTheAddressesCannotBeRead_IsNull()
    {
        var source = new FixedLocalAddressSource([IPAddress.Loopback]) { Fail = true };

        Assert.Null(source.ReadOrNull());
    }
}
