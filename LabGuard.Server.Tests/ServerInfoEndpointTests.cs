using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using LabGuard.Server.Network;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

public class ServerInfoEndpointTests
{
    private static async Task<HttpClient> SignedInAsync(TestApp app)
    {
        var client = app.CreateBrowser();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = TestApp.Username, password = TestApp.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    [Fact]
    public async Task ServerInfo_ReturnsBothPorts_AndAddressesThatAreLanIpv4()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var ports = app.Services.GetRequiredService<ServerPorts>();

        var response = await admin.GetAsync("/api/server-info");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(ports.ClientPort, body.GetProperty("clientPort").GetInt32());
        Assert.Equal(ports.AdminPort, body.GetProperty("adminPort").GetInt32());

        // This machine's addresses depend on its network, so only their shape is checked: the primary may be null.
        var primary = body.GetProperty("primaryAddress");
        var others = body.GetProperty("otherAddresses").EnumerateArray().Select(a => a.GetString()!).ToList();
        var addresses = new List<string>(others);
        if (primary.ValueKind == JsonValueKind.String)
        {
            Assert.DoesNotContain(primary.GetString(), others);
            addresses.Add(primary.GetString()!);
        }
        else
        {
            Assert.Equal(JsonValueKind.Null, primary.ValueKind);
        }

        foreach (var address in addresses)
        {
            var parsed = IPAddress.Parse(address);
            Assert.Equal(AddressFamily.InterNetwork, parsed.AddressFamily);
            Assert.False(IPAddress.IsLoopback(parsed));
            Assert.False(address.StartsWith("169.254.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ServerInfo_FromThePcPort_Returns404()
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient();

        Assert.Equal(HttpStatusCode.NotFound, (await pcClient.GetAsync("/api/server-info")).StatusCode);
    }

    [Fact]
    public async Task ServerInfo_WithoutSignIn_Returns401()
    {
        using var app = new TestApp();
        using var anonymous = app.CreateBrowser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/server-info")).StatusCode);
    }
}
