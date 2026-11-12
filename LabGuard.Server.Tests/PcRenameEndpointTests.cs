using System.Net;
using System.Net.Http.Json;
using System.Text;
using LabGuard.Server.Data;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;
using static LabGuard.Server.Tests.WallpaperTestSupport;

namespace LabGuard.Server.Tests;

// The administrator renames a PC by giving it a new number. The "PC-" prefix is fixed, and a name another PC or a waiting
// registration request holds is refused.
public class PcRenameEndpointTests
{
    private const string NumberMessage = "Number must be a whole number from 1 to 999999999.";

    private static Task<HttpResponseMessage> RenameAsync(HttpClient client, int pcId, object body) =>
        client.PutAsJsonAsync($"/api/pcs/{pcId}/name", body);

    private static async Task<string> StoredNameAsync(TestApp app, int pcId) => (await StoredPcAsync(app, pcId)).Name;

    private static async Task<string[]> ListedNamesAsync(HttpClient admin) =>
        (await JsonOf(await admin.GetAsync("/api/pcs"))).EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToArray();

    private static Task AddRegistrationRequestAsync(TestApp app, string name, RegistrationRequestStatus status, DateTimeOffset requestedAt) =>
        app.WithDbAsync(async db =>
        {
            db.PcRegistrationRequests.Add(new PcRegistrationRequest
            {
                Name = name,
                Commitment = TestApp.NewCommitment(),
                ServerNonce = TestApp.NewNonce(),
                TokenHash = Guid.NewGuid().ToString("N"),
                RequestedAtUtc = requestedAt,
                Status = status,
            });
            return await db.SaveChangesAsync();
        });

    [Fact]
    public async Task Rename_WithoutCookie_Returns401AndChangesNothing()
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();
        var pc = await AddPcAsync(app, "PC-1");

        var response = await RenameAsync(browser, pc.Id, new { number = 9 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("PC-1", await StoredNameAsync(app, pc.Id));
    }

    [Fact]
    public async Task Rename_FromThePcPort_Returns404AndChangesNothing()
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app, "PC-1");

        var response = await RenameAsync(pcClient, pc.Id, new { number = 9 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PC-1", await StoredNameAsync(app, pc.Id));
    }

    [Fact]
    public async Task Rename_ToAFreeNumber_Returns200WithTheNewName()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-1");

        var response = await RenameAsync(admin, pc.Id, new { number = 9 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PC-9", (await JsonOf(response)).GetProperty("name").GetString());
        Assert.Equal("PC-9", await StoredNameAsync(app, pc.Id));
    }

    [Fact]
    public async Task Rename_ListsThePcByItsNewNumber()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var first = await AddPcAsync(app, "PC-1");
        for (var number = 2; number <= 8; number++)
        {
            await AddPcAsync(app, $"PC-{number}");
        }

        await AddPcAsync(app, "PC-10");

        var response = await RenameAsync(admin, first.Id, new { number = 9 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["PC-2", "PC-3", "PC-4", "PC-5", "PC-6", "PC-7", "PC-8", "PC-9", "PC-10"],
            await ListedNamesAsync(admin));
    }

    [Fact]
    public async Task Rename_ToItsCurrentName_Returns200AndChangesNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-9");

        var response = await RenameAsync(admin, pc.Id, new { number = 9 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PC-9", (await JsonOf(response)).GetProperty("name").GetString());
        Assert.Equal("PC-9", await StoredNameAsync(app, pc.Id));
    }

    [Fact]
    public async Task Rename_ToItsOwnNameInAnotherCase_StoresTheStandardSpelling()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "pc-9");

        var response = await RenameAsync(admin, pc.Id, new { number = 9 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PC-9", await StoredNameAsync(app, pc.Id));
    }

    [Fact]
    public async Task Rename_ToAnotherPcsName_Returns409AndChangesNothing()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-1");
        await AddPcAsync(app, "PC-9");

        var response = await RenameAsync(admin, pc.Id, new { number = 9 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PC-9 is already used by another PC.", await ErrorOfAsync(response));
        Assert.Equal("PC-1", await StoredNameAsync(app, pc.Id));
    }

    [Fact]
    public async Task Rename_ToAnotherPcsNameInAnotherCase_Returns409()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-1");
        await AddPcAsync(app, "pc-9");

        var response = await RenameAsync(admin, pc.Id, new { number = 9 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PC-9 is already used by another PC.", await ErrorOfAsync(response));
        Assert.Equal("PC-1", await StoredNameAsync(app, pc.Id));
    }

    [Theory]
    [InlineData("PC-9")]
    [InlineData("pc-9")]
    public async Task Rename_ToAPendingRegistrationRequestsName_Returns409(string requestName)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-1");
        await AddRegistrationRequestAsync(app, requestName, RegistrationRequestStatus.Pending, app.Time.GetUtcNow());

        var response = await RenameAsync(admin, pc.Id, new { number = 9 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("PC-9 is already used by another PC.", await ErrorOfAsync(response));
        Assert.Equal("PC-1", await StoredNameAsync(app, pc.Id));
    }

    [Fact]
    public async Task Rename_ToAnExpiredRegistrationRequestsName_Succeeds()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-1");
        var expiredAt = app.Time.GetUtcNow() - PcRegistrationRequest.PendingLifetime - TimeSpan.FromSeconds(1);
        await AddRegistrationRequestAsync(app, "PC-9", RegistrationRequestStatus.Pending, expiredAt);

        var response = await RenameAsync(admin, pc.Id, new { number = 9 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PC-9", await StoredNameAsync(app, pc.Id));
    }

    [Theory]
    [InlineData(RegistrationRequestStatus.Approved)]
    [InlineData(RegistrationRequestStatus.Denied)]
    [InlineData(RegistrationRequestStatus.Cancelled)]
    public async Task Rename_ToADecidedRegistrationRequestsName_Succeeds(RegistrationRequestStatus status)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-1");
        await AddRegistrationRequestAsync(app, "PC-9", status, app.Time.GetUtcNow());

        var response = await RenameAsync(admin, pc.Id, new { number = 9 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PC-9", await StoredNameAsync(app, pc.Id));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"number":null}""")]
    [InlineData("""{"number":0}""")]
    [InlineData("""{"number":-1}""")]
    [InlineData("""{"number":1000000000}""")]
    public async Task Rename_WithANumberOutOfRange_Returns400AndChangesNothing(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-1");

        var response = await admin.PutAsync($"/api/pcs/{pc.Id}/name", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(NumberMessage, await ErrorOfAsync(response));
        Assert.Equal("PC-1", await StoredNameAsync(app, pc.Id));
    }

    [Theory]
    [InlineData("application/json", """{"number":99999999999}""")]
    [InlineData("application/json", """{"number":9.5}""")]
    [InlineData("application/json", "null")]
    [InlineData("application/json", "{not json")]
    [InlineData("text/plain", """{"number":9}""")]
    public async Task Rename_WithAnUnreadableBody_Returns400AndChangesNothing(string contentType, string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-1");

        var response = await admin.PutAsync($"/api/pcs/{pc.Id}/name", new StringContent(body, Encoding.UTF8, contentType));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, await ErrorOfAsync(response));
        Assert.Equal("PC-1", await StoredNameAsync(app, pc.Id));
    }

    [Fact]
    public async Task Rename_UnknownPc_Returns404()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await RenameAsync(admin, 999, new { number = 9 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("PC not found.", await ErrorOfAsync(response));
    }

    [Fact]
    public async Task Rename_KeepsTheNameStoredWithSessions()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app, "PC-1");
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);

        await RenameAsync(admin, pc.Id, new { number = 9 });

        Assert.Equal("PC-1", (await StoredSessionAsync(app, sessionId)).PcName);
        Assert.Equal("PC-9", await StoredNameAsync(app, pc.Id));
    }
}
