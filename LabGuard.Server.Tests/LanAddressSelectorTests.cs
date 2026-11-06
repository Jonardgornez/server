using System.Net;
using LabGuard.Server.Network;

namespace LabGuard.Server.Tests;

public class LanAddressSelectorTests
{
    private static LanAdapter Adapter(
        string name,
        string description = "Physical adapter",
        bool up = true,
        bool loopback = false,
        bool gateway = false,
        params string[] addresses) =>
        new(name, description, up, loopback, gateway, addresses.Select(IPAddress.Parse).ToList());

    [Fact]
    public void Select_PrefersAnAdapterWithAGateway()
    {
        var result = LanAddressSelector.Select(
        [
            Adapter("Ethernet", gateway: false, addresses: "10.0.0.5"),
            Adapter("Wi-Fi", gateway: true, addresses: "192.168.1.20"),
        ]);

        Assert.Equal("192.168.1.20", result.Primary);
        Assert.Equal(["10.0.0.5"], result.Others);
    }

    [Fact]
    public void Select_SkipsAVirtualAdapterWithAGateway_WhenAPhysicalOneExists()
    {
        var result = LanAddressSelector.Select(
        [
            Adapter("vEthernet (Default Switch)", gateway: true, addresses: "172.20.0.1"),
            Adapter("Wi-Fi", gateway: true, addresses: "192.168.1.20"),
        ]);

        Assert.Equal("192.168.1.20", result.Primary);
        Assert.Equal(["172.20.0.1"], result.Others);
    }

    [Fact]
    public void Select_FallsBackToANonVirtualAdapterWithoutAGateway()
    {
        var result = LanAddressSelector.Select(
        [
            Adapter("vEthernet (WSL)", gateway: true, addresses: "172.25.0.1"),
            Adapter("Ethernet", gateway: false, addresses: "10.0.0.5"),
        ]);

        Assert.Equal("10.0.0.5", result.Primary);
    }

    [Theory]
    [InlineData("Hyper-V Virtual Ethernet Adapter")]
    [InlineData("vEthernet adapter")]
    [InlineData("VirtualBox Host-Only Ethernet Adapter")]
    [InlineData("VMware Network Adapter VMnet8")]
    [InlineData("Windows Subsystem for Linux (WSL)")]
    [InlineData("Microsoft KM-TEST Loopback Adapter")]
    [InlineData("TAP-Windows Adapter V9")]
    [InlineData("Bluetooth Device (Personal Area Network)")]
    public void Select_NeverPicksAVirtualAdapterForPrimary_WhenMarkedByDescription(string description)
    {
        var result = LanAddressSelector.Select(
        [
            Adapter("Ethernet 3", description, gateway: true, addresses: "172.30.0.1"),
            Adapter("Ethernet", gateway: false, addresses: "10.0.0.5"),
        ]);

        Assert.Equal("10.0.0.5", result.Primary);
    }

    [Theory]
    [InlineData("vEthernet (Default Switch)")]
    [InlineData("VirtualBox Host-Only Network")]
    [InlineData("tap0901")]
    public void Select_NeverPicksAVirtualAdapterForPrimary_WhenMarkedByName(string name)
    {
        var result = LanAddressSelector.Select(
        [
            Adapter(name, gateway: true, addresses: "172.30.0.1"),
            Adapter("Ethernet", gateway: false, addresses: "10.0.0.5"),
        ]);

        Assert.Equal("10.0.0.5", result.Primary);
    }

    [Fact]
    public void Select_WithOnlyVirtualAdapters_HasNoPrimary_ButListsTheirAddresses()
    {
        var result = LanAddressSelector.Select(
        [
            Adapter("vEthernet (Default Switch)", gateway: true, addresses: "172.20.0.1"),
        ]);

        Assert.Null(result.Primary);
        Assert.Equal(["172.20.0.1"], result.Others);
    }

    [Fact]
    public void Select_IgnoresAdaptersThatAreDown()
    {
        var result = LanAddressSelector.Select(
        [
            Adapter("Wi-Fi", up: false, gateway: true, addresses: "192.168.1.20"),
            Adapter("Ethernet", gateway: false, addresses: "10.0.0.5"),
        ]);

        Assert.Equal("10.0.0.5", result.Primary);
        Assert.Empty(result.Others);
    }

    [Fact]
    public void Select_IgnoresLoopbackAdapters()
    {
        var result = LanAddressSelector.Select(
        [
            Adapter("Loopback Pseudo-Interface 1", loopback: true, gateway: true, addresses: "127.0.0.1"),
        ]);

        Assert.Null(result.Primary);
        Assert.Empty(result.Others);
    }

    [Fact]
    public void Select_IgnoresLinkLocalAndIpv6Addresses()
    {
        var result = LanAddressSelector.Select(
        [
            Adapter("Ethernet", gateway: false, addresses: ["169.254.10.10", "fe80::1", "192.168.1.9"]),
        ]);

        Assert.Equal("192.168.1.9", result.Primary);
        Assert.Empty(result.Others);
    }

    [Fact]
    public void Select_WithAnAdapterHavingSeveralAddresses_ListsTheRestAsOthers()
    {
        var result = LanAddressSelector.Select(
        [
            Adapter("Wi-Fi", gateway: true, addresses: ["192.168.1.20", "192.168.1.21", "10.1.1.1"]),
        ]);

        Assert.Equal("192.168.1.20", result.Primary);
        Assert.Equal(["192.168.1.21", "10.1.1.1"], result.Others);
    }

    [Fact]
    public void Select_ListsEachAddressOnce_AndNeverRepeatsThePrimary()
    {
        var result = LanAddressSelector.Select(
        [
            Adapter("Wi-Fi", gateway: true, addresses: "192.168.1.20"),
            Adapter("Ethernet", gateway: false, addresses: ["192.168.1.20", "10.0.0.5"]),
        ]);

        Assert.Equal("192.168.1.20", result.Primary);
        Assert.Equal(["10.0.0.5"], result.Others);
    }

    [Fact]
    public void Select_WithNoAdapters_ReturnsNoPrimaryAndNoOthers()
    {
        var result = LanAddressSelector.Select([]);

        Assert.Null(result.Primary);
        Assert.Empty(result.Others);
    }
}
