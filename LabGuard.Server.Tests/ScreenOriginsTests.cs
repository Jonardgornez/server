using LabGuard.Server.Network;
using LabGuard.Server.Screen;
using Microsoft.Extensions.Configuration;

namespace LabGuard.Server.Tests;

public class ScreenOriginsTests
{
    private static readonly ServerPorts Ports = new(AdminPort: 5000, ClientPort: 5001);

    private static ScreenOrigins Origins(params string[] configured)
    {
        var values = new Dictionary<string, string?>();
        for (var index = 0; index < configured.Length; index++)
        {
            values[$"Screen:AllowedOrigins:{index}"] = configured[index];
        }

        return new ScreenOrigins(Ports, new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://[::1]:5000")]
    public void IsAllowed_AlwaysAcceptsTheAdminPortOnLoopback(string origin)
    {
        Assert.True(Origins().IsAllowed(origin));
    }

    [Fact]
    public void IsAllowed_AcceptsTheConfiguredOrigins()
    {
        var origins = Origins("http://localhost:5173", "http://localhost:4173");

        Assert.True(origins.IsAllowed("http://localhost:5173"));
        Assert.True(origins.IsAllowed("http://localhost:4173"));
    }

    [Fact]
    public void IsAllowed_WithNothingConfigured_RefusesThePreviewAndTheDevServer()
    {
        var origins = Origins();

        Assert.False(origins.IsAllowed("http://localhost:4173"));
        Assert.False(origins.IsAllowed("http://localhost:5173"));
    }

    [Theory]
    [InlineData("http://192.168.1.5:5173")]
    [InlineData("http://0.0.0.0:5173")]
    [InlineData("http://evil.example:4173")]
    [InlineData("https://evil.example")]
    [InlineData("ws://localhost:5173")]
    [InlineData("http://localhost:5173/")]
    [InlineData("http://localhost:5173/admin")]
    [InlineData("HTTP://LOCALHOST:5173")]
    [InlineData("http://localhost:80")]
    [InlineData("http://localhost.evil.example:5173")]
    [InlineData("http://localhost@evil.example:5173")]
    [InlineData(" http://localhost:5173")]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("null")]
    public void IsAllowed_IgnoresAnyConfiguredEntryThatIsNotAnExactLoopbackOrigin(string configured)
    {
        var origins = Origins(configured);

        Assert.False(origins.IsAllowed(configured));
        Assert.False(origins.IsAllowed("http://192.168.1.5:5173"));
    }

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://127.0.0.1:4173")]
    [InlineData("http://[::1]:4173")]
    [InlineData("https://localhost:8443")]
    public void IsExactLoopbackOrigin_AcceptsTheCanonicalLoopbackForms(string text)
    {
        Assert.True(ScreenOrigins.IsExactLoopbackOrigin(text));
    }

    [Fact]
    public void IsAllowed_RefusesAnOriginMissingFromTheRequest()
    {
        Assert.False(Origins("http://localhost:5173").IsAllowed(null));
    }

    [Fact]
    public void IsAllowed_FollowsTheConfiguredAdminPort()
    {
        var origins = new ScreenOrigins(new ServerPorts(AdminPort: 6100, ClientPort: 6101), new ConfigurationBuilder().Build());

        Assert.True(origins.IsAllowed("http://localhost:6100"));
        Assert.False(origins.IsAllowed("http://localhost:5000"));
    }
}
