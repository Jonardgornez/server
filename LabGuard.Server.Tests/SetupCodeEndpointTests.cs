using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Clients;
using LabGuard.Server.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Tests;

public class SetupCodeEndpointTests
{
    private const string WrongCodeMessage = "That setup code isn't right.";
    private const string CodePattern = "^[A-Z2-7]{4}(-[A-Z2-7]{4}){5}$";

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static async Task<HttpClient> SignedInAsync(TestApp app)
    {
        var client = app.CreateBrowser();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = TestApp.Username, password = TestApp.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static Task<HttpResponseMessage> Register(TestApp app, HttpClient pc, string? name, string? secret) =>
        pc.PostAsync("/api/client/register", app.RegistrationBody(name, secret));

    private static async Task AssertWrongCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(WrongCodeMessage, body.GetProperty("error").GetString());
        Assert.Single(body.EnumerateObject());
    }

    // Register

    [Fact]
    public async Task Register_WithRightSecret_Succeeds()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient();

        Assert.Equal(HttpStatusCode.Created, (await Register(app, pc, "PC-05", app.SetupSecret)).StatusCode);
    }

    [Fact]
    public async Task Register_WithWrongSecret_Returns403AndStoresNothing()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient();
        var wrong = app.SetupSecret[0] == 'A' ? "BBBBBBBBBB" : "AAAAAAAAAA";

        await AssertWrongCodeAsync(await Register(app, pc, "PC-05", wrong));

        Assert.Equal(0, await app.WithDbAsync(db => db.Pcs.CountAsync()));
    }

    [Fact]
    public async Task Register_WithMissingOrEmptySecret_Returns403()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient();

        await AssertWrongCodeAsync(await pc.PostAsJsonAsync("/api/client/register", new { name = "PC-05" }));
        await AssertWrongCodeAsync(await Register(app, pc, "PC-05", ""));
        await AssertWrongCodeAsync(await Register(app, pc, "PC-05", null));
    }

    [Fact]
    public async Task Register_AcceptsLowercaseAndHyphenatedSecret()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient();
        var secret = app.SetupSecret;

        Assert.Equal(HttpStatusCode.Created, (await Register(app, pc, "PC-01", secret.ToLowerInvariant())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Register(app, pc, "PC-02", $"{secret[..5]}-{secret[5..]}")).StatusCode);
    }

    [Fact]
    public async Task Register_WithTheWholeSetupCode_IsRejected_OnlyTheSecretPartCounts()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient();

        await AssertWrongCodeAsync(await Register(app, pc, "PC-01", app.Channel.SetupCode));
    }

    [Fact]
    public async Task Register_WrongSecretWithTakenName_Returns403Not409()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient();
        await Register(app, pc, "PC-05", app.SetupSecret);

        await AssertWrongCodeAsync(await Register(app, pc, "PC-05", "AAAAAAAAAA"));
        Assert.Equal(HttpStatusCode.Conflict, (await Register(app, pc, "PC-05", app.SetupSecret)).StatusCode);
    }

    [Fact]
    public async Task Register_WrongSecretWithInvalidName_Returns403Not400()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient();

        await AssertWrongCodeAsync(await Register(app, pc, "bad name!", "AAAAAAAAAA"));
    }

    [Fact]
    public async Task Register_WrongCodesCountTowardTheRateLimit()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient();

        for (var i = 0; i < ClientEndpoints.MaxRegistrationsPerMinute; i++)
        {
            await AssertWrongCodeAsync(await Register(app, pc, "PC-01", "AAAAAAAAAA"));
        }

        var limited = await Register(app, pc, "PC-01", app.SetupSecret);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Register_ErrorsDoNotRevealTheSecret()
    {
        using var app = new TestApp();
        using var pc = app.CreatePcClient();

        var text = await (await Register(app, pc, "PC-01", "AAAAAAAAAA")).Content.ReadAsStringAsync();

        Assert.DoesNotContain(app.SetupSecret, text);
        Assert.DoesNotContain(app.SetupSecret, string.Join('\n', app.Logs.Entries));
    }

    // Admin endpoints

    [Fact]
    public async Task SetupCode_WithoutACookie_Returns401()
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/pcs/setup-code")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.PostAsJsonAsync("/api/pcs/setup-code/regenerate", new { })).StatusCode);
    }

    [Fact]
    public async Task SetupCode_ReturnsTheCurrentCode()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await admin.GetAsync("/api/pcs/setup-code");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Single(body.EnumerateObject());
        Assert.Matches(CodePattern, body.GetProperty("code").GetString());
        Assert.Equal(app.Channel.SetupCode, body.GetProperty("code").GetString());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Regenerate_ChangesOnlyTheSecret_AndTheOldSecretStopsWorking()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pc = app.CreatePcClient();
        var oldCode = app.Channel.SetupCode;
        var oldSecret = app.SetupSecret;
        Assert.Equal(HttpStatusCode.Created, (await Register(app, pc, "PC-01", oldSecret)).StatusCode);

        var response = await admin.PostAsJsonAsync("/api/pcs/setup-code/regenerate", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var newCode = (await JsonOf(response)).GetProperty("code").GetString()!;
        Assert.Matches(CodePattern, newCode);
        Assert.Equal(PcSetupCode.Normalize(oldCode)[..14], PcSetupCode.Normalize(newCode)[..14]);
        Assert.NotEqual(oldSecret, PcSetupCode.Normalize(newCode)[14..]);
        Assert.Equal(newCode, (await JsonOf(await admin.GetAsync("/api/pcs/setup-code"))).GetProperty("code").GetString());
        await AssertWrongCodeAsync(await Register(app, pc, "PC-02", oldSecret));
        Assert.Equal(HttpStatusCode.Created, (await Register(app, pc, "PC-02", app.SetupSecret)).StatusCode);
    }

    [Fact]
    public async Task Regenerate_DoesNotAffectRegisteredPcs()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var pc = app.CreatePcClient();
        var registered = await JsonOf(await Register(app, pc, "PC-01", app.SetupSecret));
        var token = registered.GetProperty("token").GetString();

        await admin.PostAsJsonAsync("/api/pcs/setup-code/regenerate", new { });

        using var heartbeat = new HttpRequestMessage(HttpMethod.Post, "/api/client/heartbeat");
        heartbeat.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        Assert.Equal(HttpStatusCode.OK, (await pc.SendAsync(heartbeat)).StatusCode);
    }

    [Theory]
    [InlineData("{not json", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("{}", "text/plain")]
    [InlineData("a=b", "application/x-www-form-urlencoded")]
    public async Task Regenerate_WithoutAJsonBody_Returns400AndChangesNothing(string body, string contentType)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var before = app.Channel.SetupCode;

        var response = await admin.PostAsync("/api/pcs/setup-code/regenerate", new StringContent(body, Encoding.UTF8, contentType));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, app.Channel.SetupCode);
    }

    [Fact]
    public async Task Regenerate_WithNoBody_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await admin.PostAsync("/api/pcs/setup-code/regenerate", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/pcs/setup-code")]
    [InlineData("POST", "/api/pcs/setup-code/regenerate")]
    public async Task SetupCodeEndpoints_AreNotFoundOnThePcPort_EvenWithACookie(string method, string path)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var before = app.Channel.SetupCode;
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };
        request.Headers.Add(SimulatedConnectionFilter.LocalPortHeader, app.Services.GetRequiredService<ServerPorts>().ClientPort.ToString());

        var response = await admin.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, app.Channel.SetupCode);
    }
}
