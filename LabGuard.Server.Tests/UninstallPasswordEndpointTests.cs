using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The uninstall password route: setting, clearing and reading whether it is set, its limits, who may use it,
// and the hash every PC gets back in the reply to its service report.
public class UninstallPasswordEndpointTests
{
    private const string Route = "/api/settings/uninstall-password";
    private const string ServiceReportRoute = "/api/client/service-report";
    private const string Password = "correct horse battery";
    private const string LengthError = "The uninstall password must be 8 to 128 characters.";

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string? password) =>
        client.PutAsJsonAsync(Route, new { password });

    private static Task<HttpResponseMessage> PutRawAsync(HttpClient client, string body) =>
        client.PutAsync(Route, new StringContent(body, Encoding.UTF8, "application/json"));

    private static async Task<bool> IsSetAsync(HttpClient admin)
    {
        var response = await admin.GetAsync(Route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("isSet").GetBoolean();
    }

    private static Task<ServerSettings?> StoredSettingsAsync(TestApp app) =>
        app.WithDbAsync(db => db.ServerSettingsRows.AsNoTracking().FirstOrDefaultAsync());

    private static Task<HttpResponseMessage> ReportAsync(HttpClient pc, TestPc registered) =>
        PostAsync(pc, ServiceReportRoute, registered.Token, JsonContent.Create(new { serviceVersion = "0.23.0" }));

    private static async Task<JsonElement> ReportedUninstallPasswordAsync(TestApp app)
    {
        using var pc = app.CreatePcClient(OtherPcAddress);
        var registered = await AddPcAsync(app);
        var response = await ReportAsync(pc, registered);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("uninstallPassword");
    }

    private static bool Verifies(string password, JsonElement reported)
    {
        var salt = Convert.FromBase64String(reported.GetProperty("salt").GetString()!);
        var hash = Convert.FromBase64String(reported.GetProperty("hash").GetString()!);
        var iterations = reported.GetProperty("iterations").GetInt32();
        var derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, hash.Length);
        return CryptographicOperations.FixedTimeEquals(derived, hash);
    }

    // Set, clear and read

    [Fact]
    public async Task Get_BeforeAnyPassword_ReturnsNotSet()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        Assert.False(await IsSetAsync(admin));
    }

    [Fact]
    public async Task Put_SetsThePassword_AndGetReportsItSet()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutAsync(admin, Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"isSet":true}""", await response.Content.ReadAsStringAsync());
        Assert.True(await IsSetAsync(admin));
    }

    [Fact]
    public async Task Put_WithNull_ClearsThePassword()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await PutAsync(admin, Password);

        var response = await PutAsync(admin, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"isSet":false}""", await response.Content.ReadAsStringAsync());
        Assert.False(await IsSetAsync(admin));
        var stored = await StoredSettingsAsync(app);
        Assert.Null(stored!.UninstallPasswordHash);
        Assert.Null(stored.UninstallPasswordSalt);
        Assert.Null(stored.UninstallPasswordIterations);
    }

    [Fact]
    public async Task Put_StoresAPbkdf2HashThatVerifiesTheOriginalPassword_AndNotThePassword()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        await PutAsync(admin, Password);

        var stored = await StoredSettingsAsync(app);
        Assert.Equal(600_000, stored!.UninstallPasswordIterations);
        Assert.Equal(16, Convert.FromBase64String(stored.UninstallPasswordSalt!).Length);
        Assert.Equal(32, Convert.FromBase64String(stored.UninstallPasswordHash!).Length);
        var reported = JsonSerializer.SerializeToElement(new
        {
            salt = stored.UninstallPasswordSalt,
            hash = stored.UninstallPasswordHash,
            iterations = stored.UninstallPasswordIterations,
        });
        Assert.True(Verifies(Password, reported));
        Assert.False(Verifies(Password + "x", reported));
        Assert.DoesNotContain(Password, stored.UninstallPasswordHash);
    }

    [Fact]
    public async Task Put_TheSamePasswordTwice_UsesANewSalt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await PutAsync(admin, Password);
        var first = await StoredSettingsAsync(app);

        await PutAsync(admin, Password);

        var second = await StoredSettingsAsync(app);
        Assert.NotEqual(first!.UninstallPasswordSalt, second!.UninstallPasswordSalt);
        Assert.NotEqual(first.UninstallPasswordHash, second.UninstallPasswordHash);
    }

    [Fact]
    public async Task Get_NeverReturnsTheHashOrTheSalt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await PutAsync(admin, Password);
        var stored = await StoredSettingsAsync(app);

        var body = await (await admin.GetAsync(Route)).Content.ReadAsStringAsync();
        var settingsBody = await (await admin.GetAsync("/api/settings")).Content.ReadAsStringAsync();

        Assert.Equal("""{"isSet":true}""", body);
        Assert.DoesNotContain(stored!.UninstallPasswordHash!, settingsBody);
        Assert.DoesNotContain(stored.UninstallPasswordSalt!, settingsBody);
    }

    // Saving the other settings leaves the uninstall password as it is.
    [Fact]
    public async Task PutSettings_KeepsTheUninstallPassword()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await PutAsync(admin, Password);
        var settings = await JsonOf(await admin.GetAsync("/api/settings"));

        var response = await admin.PutAsJsonAsync("/api/settings", settings);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await IsSetAsync(admin));
    }

    // Limits

    [Theory]
    [InlineData(8)]
    [InlineData(128)]
    public async Task Put_AcceptsTheLengthBoundaries(int length)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutAsync(admin, new string('a', length));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await IsSetAsync(admin));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(129)]
    public async Task Put_WithAPasswordTooShortOrTooLong_Returns400_AndChangesNothing(int length)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutAsync(admin, new string('a', length));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(LengthError, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.False(await IsSetAsync(admin));
    }

    // Spaces count as typed: seven characters padded with spaces are long enough, and are kept as they are.
    [Fact]
    public async Task Put_DoesNotTrimThePassword()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        await PutAsync(admin, " abcdef ");

        var stored = await StoredSettingsAsync(app);
        var reported = JsonSerializer.SerializeToElement(new
        {
            salt = stored!.UninstallPasswordSalt,
            hash = stored.UninstallPasswordHash,
            iterations = stored.UninstallPasswordIterations,
        });
        Assert.True(Verifies(" abcdef ", reported));
        Assert.False(Verifies("abcdef", reported));
    }

    // The PC normalizes what is typed in the uninstaller to NFC too, so a decomposed "é" set here verifies as the composed one.
    [Fact]
    public async Task Put_StoresTheHashOfTheNfcPassword()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        await PutAsync(admin, "café-password");

        var stored = await StoredSettingsAsync(app);
        var reported = JsonSerializer.SerializeToElement(new
        {
            salt = stored!.UninstallPasswordSalt,
            hash = stored.UninstallPasswordHash,
            iterations = stored.UninstallPasswordIterations,
        });
        Assert.True(Verifies("café-password", reported));
    }

    // Four decomposed accented letters are eight characters as sent, but four once normalized, which is too short.
    [Fact]
    public async Task Put_ChecksTheLengthAfterNormalizing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutAsync(admin, "éééé");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(LengthError, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.False(await IsSetAsync(admin));
    }

    [Fact]
    public async Task Put_TooShortAfterASetPassword_KeepsTheOldPassword()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await PutAsync(admin, Password);
        var before = await StoredSettingsAsync(app);

        var response = await PutAsync(admin, "short");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before!.UninstallPasswordHash, (await StoredSettingsAsync(app))!.UninstallPasswordHash);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"password":12345678}""")]
    [InlineData("""{"password":true}""")]
    [InlineData("""{"password":["abcdefgh"]}""")]
    public async Task Put_WithAMissingOrNonTextPassword_Returns400(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutRawAsync(admin, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Enter an uninstall password, or null to remove it.", (await JsonOf(response)).GetProperty("error").GetString());
        Assert.False(await IsSetAsync(admin));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{not json")]
    public async Task Put_WithAMissingNullOrMalformedBody_Returns400InvalidRequest(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutRawAsync(admin, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid request.", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_WithoutAJsonContentType_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await admin.PutAsync(Route, new StringContent($$"""{"password":"{{Password}}"}""", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await IsSetAsync(admin));
    }

    [Fact]
    public async Task Put_WithALoneSurrogateEscape_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await PutRawAsync(admin, """{"password":"abcdefgh\ud800"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("The uninstall password can't contain a character that can't be typed.", (await JsonOf(response)).GetProperty("error").GetString());
        Assert.False(await IsSetAsync(admin));
    }

    // Access

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task UninstallPassword_WithoutSignIn_Returns401_AndChangesNothing(string method)
    {
        using var app = new TestApp();
        using var anonymous = app.CreateBrowser();

        var response = method == "GET" ? await anonymous.GetAsync(Route) : await PutAsync(anonymous, Password);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(await StoredSettingsAsync(app));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task UninstallPassword_FromThePcPort_Returns404(string method)
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient();

        var response = method == "GET" ? await pcClient.GetAsync(Route) : await PutAsync(pcClient, Password);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(await StoredSettingsAsync(app));
    }

    // Service report reply

    [Fact]
    public async Task ServiceReport_WithoutAPassword_AnswersAnExplicitNull()
    {
        using var app = new TestApp();

        var reported = await ReportedUninstallPasswordAsync(app);

        Assert.Equal(JsonValueKind.Null, reported.ValueKind);
    }

    [Fact]
    public async Task ServiceReport_WithAPassword_AnswersTheSaltHashAndIterations_ThatVerifyThePassword()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await PutAsync(admin, Password);
        var stored = await StoredSettingsAsync(app);

        var reported = await ReportedUninstallPasswordAsync(app);

        Assert.Equal(stored!.UninstallPasswordSalt, reported.GetProperty("salt").GetString());
        Assert.Equal(stored.UninstallPasswordHash, reported.GetProperty("hash").GetString());
        Assert.Equal(600_000, reported.GetProperty("iterations").GetInt32());
        Assert.True(Verifies(Password, reported));
        Assert.False(Verifies("wrong password", reported));
    }

    [Fact]
    public async Task ServiceReport_AfterTheAdministratorClearsThePassword_AnswersNull()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await PutAsync(admin, Password);
        await PutAsync(admin, null);

        var reported = await ReportedUninstallPasswordAsync(app);

        Assert.Equal(JsonValueKind.Null, reported.ValueKind);
    }
}
