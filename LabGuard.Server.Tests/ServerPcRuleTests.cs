using System.Net;
using LabGuard.Server.Network;

namespace LabGuard.Server.Tests;

public class ServerPcRuleTests
{
    private static readonly IPAddress[] ThisMachine = [IPAddress.Parse("198.51.100.10"), IPAddress.Parse("fd00::10")];

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.12.0.9")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void IsServerPc_LoopbackAddress_IsTheServerPc(string address)
    {
        Assert.True(ServerPcRule.IsServerPc(address, []));
    }

    [Theory]
    [InlineData("198.51.100.10")]
    [InlineData("::ffff:198.51.100.10")]
    [InlineData("fd00::10")]
    public void IsServerPc_AnAddressOfThisMachine_IsTheServerPc(string address)
    {
        Assert.True(ServerPcRule.IsServerPc(address, ThisMachine));
    }

    [Fact]
    public void IsServerPc_AMachineAddressGivenAsIPv4Mapped_MatchesThePlainAddress()
    {
        var mapped = new[] { IPAddress.Parse("::ffff:198.51.100.10") };

        Assert.True(ServerPcRule.IsServerPc("198.51.100.10", mapped));
    }

    [Theory]
    [InlineData("203.0.113.5")]
    [InlineData("198.51.100.11")]
    [InlineData("2001:db8::5")]
    public void IsServerPc_AnotherMachinesAddress_IsNotTheServerPc(string address)
    {
        Assert.False(ServerPcRule.IsServerPc(address, ThisMachine));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    public void IsServerPc_MissingOrMalformedAddress_IsNotTheServerPc(string? address)
    {
        Assert.False(ServerPcRule.IsServerPc(address, ThisMachine));
    }

    [Fact]
    public void IsServerPc_NoMachineAddresses_OnlyLoopbackCounts()
    {
        Assert.True(ServerPcRule.IsServerPc("127.0.0.1", []));
        Assert.False(ServerPcRule.IsServerPc("198.51.100.10", []));
    }

    [Fact]
    public void NetworkInterfaceAddressSource_Read_ReturnsWithoutThrowing()
    {
        var addresses = new NetworkInterfaceAddressSource().Read();

        Assert.NotNull(addresses);
    }
}
