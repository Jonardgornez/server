using LabGuard.Server.Network;
using LabGuard.Server.Screen;

namespace LabGuard.Server.Tests;

public class ScreenOriginsTests
{
    private static readonly ServerPorts Ports = new(AdminPort: 5000, ClientPort: 5001);

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://[::1]:5000")]
    [InlineData("http://localhost:4173")]
    [InlineData("http://localhost:5173")]
    public void IsAllowed_AcceptsTheAdminPortHostsAndThePreviewAndDevOrigins_InDevelopment(string origin)
    {
        Assert.True(new ScreenOrigins(Ports, isDevelopment: true).IsAllowed(origin));
    }

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://[::1]:5000")]
    [InlineData("http://localhost:4173")]
    public void IsAllowed_InProduction_AcceptsTheAdminPortAndThePreviewOrigin(string origin)
    {
        Assert.True(new ScreenOrigins(Ports, isDevelopment: false).IsAllowed(origin));
    }

    [Theory]
    [InlineData("http://localhost:5999")]
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("https://localhost:5000")]
    [InlineData("http://LOCALHOST:5000 ")]
    [InlineData("http://localhost:5000/")]
    [InlineData("http://localhost.evil.example:5000")]
    [InlineData("http://localhost@evil.example:5000")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAllowed_RefusesEveryOtherOrigin(string? origin)
    {
        Assert.False(new ScreenOrigins(Ports, isDevelopment: true).IsAllowed(origin));
    }

    [Fact]
    public void IsAllowed_InProduction_RefusesTheDevServer()
    {
        Assert.False(new ScreenOrigins(Ports, isDevelopment: false).IsAllowed("http://localhost:5173"));
    }

    [Fact]
    public void IsAllowed_FollowsTheConfiguredAdminPort()
    {
        var origins = new ScreenOrigins(new ServerPorts(AdminPort: 6100, ClientPort: 6101), isDevelopment: false);

        Assert.True(origins.IsAllowed("http://localhost:6100"));
        Assert.False(origins.IsAllowed("http://localhost:5000"));
    }
}
