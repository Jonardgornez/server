using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using static LabGuard.Server.Tests.PcShutdownEndpointTests;

namespace LabGuard.Server.Tests;

// The administrator chooses the Restrictions policies and drives of PCs. Every named PC changes, offline ones too, since the PC's service
// picks it up from its next report. Only the listed keys change, so PCs that differ keep their other choices.
public class PcRestrictionEndpointTests
{
    private const string RestrictionsPath = "/api/pcs/restrictions";

    private static Task<HttpResponseMessage> SetAsync(
        HttpClient client,
        int[] ids,
        Dictionary<string, bool>? policies = null,
        Dictionary<string, bool>? hiddenDrives = null,
        Dictionary<string, bool>? disabledDrives = null,
        bool applyNow = false) =>
        client.PostAsJsonAsync(RestrictionsPath, new { ids, policies, hiddenDrives, disabledDrives, applyNow });

    private static Dictionary<string, bool> On(params string[] keys) => keys.ToDictionary(key => key, _ => true);

    private static Dictionary<string, bool> Off(params string[] keys) => keys.ToDictionary(key => key, _ => false);

    private static async Task<(string Policies, string Hidden, string Disabled, int Generation)> StoredAsync(TestApp app, int pcId)
    {
        var pc = await StoredPcAsync(app, pcId);
        return (pc.RestrictedPolicies, pc.HiddenDrives, pc.DisabledDrives, pc.ExplorerRestartGeneration);
    }

    [Fact]
    public async Task Route_WithoutCookie_Returns401()
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();
        var pc = await AddPcAsync(app);

        var response = await SetAsync(browser, [pc.Id], On("control-panel"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(("", "", "", 0), await StoredAsync(app, pc.Id));
    }

    [Fact]
    public async Task Route_FromThePcPort_Returns404()
    {
        using var app = new TestApp();
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);

        var response = await SetAsync(pcClient, [pc.Id], On("control-panel"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(("", "", "", 0), await StoredAsync(app, pc.Id));
    }

    [Theory]
    [InlineData("text/plain", "{}")]
    [InlineData("application/x-www-form-urlencoded", "")]
    [InlineData("application/json", "")]
    [InlineData("application/json", "null")]
    [InlineData("application/json", "{not json")]
    public async Task Route_WithoutAJsonBody_Returns400AndChangesNothing(string contentType, string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);

        using var content = new StringContent(body, Encoding.UTF8, contentType);
        var response = await admin.PostAsync(RestrictionsPath, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(("", "", "", 0), await StoredAsync(app, pc.Id));
    }

    // Policies that have their own routes, or are not built, are refused here. So are drive keys other than one capital letter.
    [Theory]
    [InlineData("""{"policies":{"control-panel":true},"applyNow":false}""")]
    [InlineData("""{"ids":[],"policies":{"control-panel":true},"applyNow":false}""")]
    [InlineData("""{"ids":"one","applyNow":false}""")]
    [InlineData("""{"ids":[1],"policies":{"control-panel":true}}""")]
    [InlineData("""{"ids":[1],"applyNow":null}""")]
    [InlineData("""{"ids":[1],"applyNow":"yes"}""")]
    [InlineData("""{"ids":[1],"policies":{"control-panel":"yes"},"applyNow":false}""")]
    [InlineData("""{"ids":[1],"policies":{"control-panel":null},"applyNow":false}""")]
    [InlineData("""{"ids":[1],"policies":["control-panel"],"applyNow":false}""")]
    [InlineData("""{"ids":[1],"policies":{"unknown":true},"applyNow":false}""")]
    [InlineData("""{"ids":[1],"policies":{"task-manager":true},"applyNow":false}""")]
    [InlineData("""{"ids":[1],"policies":{"fast-user-switch":true},"applyNow":false}""")]
    [InlineData("""{"ids":[1],"policies":{"file-folder-protection":true},"applyNow":false}""")]
    [InlineData("""{"ids":[1],"policies":{"control-panel":true,"Run-Command":true},"applyNow":false}""")]
    [InlineData("""{"ids":[1],"hiddenDrives":{"c":true},"applyNow":false}""")]
    [InlineData("""{"ids":[1],"hiddenDrives":{"CD":true},"applyNow":false}""")]
    [InlineData("""{"ids":[1],"disabledDrives":{"1":true},"applyNow":false}""")]
    [InlineData("""{"ids":[1],"disabledDrives":{"":true},"applyNow":false}""")]
    public async Task Route_InvalidBody_Returns400AndChangesNothing(string body)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        Assert.Equal(1, pc.Id);

        var response = await admin.PostAsync(RestrictionsPath, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(InvalidRequestMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(("", "", "", 0), await StoredAsync(app, pc.Id));
    }

    [Fact]
    public async Task Route_MoreThanTheLimit_Returns400()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        var response = await SetAsync(admin, Enumerable.Range(1, 1001).ToArray(), On("control-panel"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Set_ReachesOnlineAndOfflinePcs_AndSkipsUnknownOnes()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var online = await AddPcAsync(app, "PC-01");
        var offline = await AddPcAsync(app, "PC-02", seenAgo: TimeSpan.FromHours(2));
        var untouched = await AddPcAsync(app, "PC-03");

        var response = await SetAsync(admin, [online.Id, offline.Id, 999], On("control-panel", "registry-editor"), On("D", "C"), On("E"));

        Assert.Equal(2, await AffectedAsync(response));
        Assert.Equal(("registry-editor,control-panel", "CD", "E", 0), await StoredAsync(app, online.Id));
        Assert.Equal(("registry-editor,control-panel", "CD", "E", 0), await StoredAsync(app, offline.Id));
        Assert.Equal(("", "", "", 0), await StoredAsync(app, untouched.Id));
    }

    // Two PCs with different choices: changing one policy keeps each PC's others.
    [Fact]
    public async Task Set_ChangesOnlyTheListedKeys()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var first = await AddPcAsync(app, "PC-01");
        var second = await AddPcAsync(app, "PC-02");
        await SetAsync(admin, [first.Id], On("run-command"), On("D"));
        await SetAsync(admin, [second.Id], On("add-printer"), disabledDrives: On("F"));

        var response = await SetAsync(admin, [first.Id, second.Id], On("windows-logout"), Off("D"));

        Assert.Equal(2, await AffectedAsync(response));
        Assert.Equal(("run-command,windows-logout", "", "", 0), await StoredAsync(app, first.Id));
        Assert.Equal(("add-printer,windows-logout", "", "F", 0), await StoredAsync(app, second.Id));
    }

    [Fact]
    public async Task Allow_ClearsIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        await SetAsync(admin, [pc.Id], On("command-prompt"), On("C"), On("C"));

        var response = await SetAsync(admin, [pc.Id], Off("command-prompt"), Off("C"), Off("C"));

        Assert.Equal(1, await AffectedAsync(response));
        Assert.Equal(("", "", "", 0), await StoredAsync(app, pc.Id));
    }

    // Each apply-now raises the count once, so a PC that missed one still sees a value it has not handled.
    [Fact]
    public async Task ApplyNow_RaisesTheExplorerRestartCount_OfTheNamedPcsOnly()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-01");
        var other = await AddPcAsync(app, "PC-02");

        await SetAsync(admin, [pc.Id], On("control-panel"), applyNow: true);
        await SetAsync(admin, [pc.Id], applyNow: true);
        await SetAsync(admin, [pc.Id, other.Id], Off("control-panel"), applyNow: false);

        Assert.Equal(2, (await StoredAsync(app, pc.Id)).Generation);
        Assert.Equal(0, (await StoredAsync(app, other.Id)).Generation);
    }

    // A restrictions change another administrator commits on its own connection, as a raw statement, standing in for their request.
    private static async Task CommitCompetingRestrictionsAsync(DbContext context, int pcId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(context.Database.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Pcs SET HiddenDrives = 'Z', RestrictionsVersion = RestrictionsVersion + 1 WHERE Id = @id";
        command.Parameters.AddWithValue("@id", pcId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Commits the competing change after this request has read the PC, right before it opens the transaction for its writes. The
    // transaction is BEGIN IMMEDIATE, so nothing else can write once it is open.
    private sealed class BeforeRestrictionsWriteInterceptor : DbTransactionInterceptor
    {
        public int PcId { get; set; }

        public int Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (Fired == 0 && PcId != 0 && eventData.Context is { } context)
            {
                Fired++;
                await CommitCompetingRestrictionsAsync(context, PcId, cancellationToken);
            }

            return result;
        }
    }

    // Commits the competing change right before another save of the PC, such as a login or a rename, after it has read the PC.
    private sealed class BeforePcSaveInterceptor : SaveChangesInterceptor
    {
        public int PcId { get; set; }

        public int Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Fired == 0 && eventData.Context is { } context && context.ChangeTracker.Entries<Pc>().Any(e => e.State == EntityState.Modified))
            {
                Fired++;
                await CommitCompetingRestrictionsAsync(context, PcId, cancellationToken);
            }

            return result;
        }
    }

    private static TestApp AppWith(IInterceptor interceptor) =>
        new(configureServices: services =>
            // EF Core takes interceptors from the options, not from DI, so the test adds this one to the context's options.
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(interceptor)));

    // The other change lands between this request's read and its write. The write is refused and the patch runs again, so both survive.
    [Fact]
    public async Task Set_WhenAnotherChangeLandsFirst_KeepsBothChanges()
    {
        var competitor = new BeforeRestrictionsWriteInterceptor();
        using var app = AppWith(competitor);
        var pc = await AddPcAsync(app);
        using var admin = await SignedInAsync(app);
        competitor.PcId = pc.Id;

        var response = await SetAsync(admin, [pc.Id], hiddenDrives: On("C"));

        Assert.Equal(1, competitor.Fired);
        Assert.Equal(1, await AffectedAsync(response));
        Assert.Equal("CZ", (await StoredAsync(app, pc.Id)).Hidden);
    }

    // A restrictions change between a student login's read and save of the PC never fails the login.
    [Fact]
    public async Task ARestrictionsChange_DuringALogin_DoesNotFailTheLogin()
    {
        var competitor = new BeforePcSaveInterceptor();
        using var app = AppWith(competitor);
        using var pcClient = app.CreatePcClient(OtherPcAddress);
        var pc = await AddPcAsync(app);
        competitor.PcId = pc.Id;
        await AddAccountAsync(app);

        var response = await LoginAsync(pcClient, pc);

        Assert.Equal(1, competitor.Fired);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Z", (await StoredAsync(app, pc.Id)).Hidden);
    }

    // RestrictionsVersion is checked only by the Restrictions route. As an EF concurrency token it would make every other save of a PC,
    // such as a rename or a login, fail after a restrictions change.
    [Fact]
    public async Task NoPcColumn_IsAConcurrencyToken()
    {
        using var app = new TestApp();

        var tokens = await app.WithDbAsync(db => Task.FromResult(
            db.Model.FindEntityType(typeof(Pc))!.GetProperties().Where(property => property.IsConcurrencyToken).Select(property => property.Name).ToList()));

        Assert.Empty(tokens);
    }

    // A rename after a restrictions change still works, and keeps the change.
    [Fact]
    public async Task ARenameAfterARestrictionsChange_Works()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-1");
        await SetAsync(admin, [pc.Id], hiddenDrives: On("Z"));

        var response = await admin.PutAsJsonAsync($"/api/pcs/{pc.Id}/name", new { number = 9 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PC-9", (await StoredPcAsync(app, pc.Id)).Name);
        Assert.Equal("Z", (await StoredAsync(app, pc.Id)).Hidden);
    }

    // Many changes at once to the same PC: each one survives.
    [Fact]
    public async Task Set_ManyAtOnce_LosesNoChange()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        var letters = "DEFGHIJK".Select(letter => letter.ToString()).ToArray();

        var responses = await Task.WhenAll(letters.Select(letter => SetAsync(admin, [pc.Id], hiddenDrives: On(letter))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal("DEFGHIJK", (await StoredAsync(app, pc.Id)).Hidden);
    }

    [Fact]
    public async Task ApplyNow_AfterTheLargestCount_WrapsToZero()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app);
        await app.WithDbAsync(db => db.Pcs
            .Where(p => p.Id == pc.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.ExplorerRestartGeneration, int.MaxValue)
                .SetProperty(p => p.RestrictionsVersion, int.MaxValue)));

        await SetAsync(admin, [pc.Id], On("run-command"), applyNow: true);

        var stored = await StoredPcAsync(app, pc.Id);
        Assert.Equal(0, stored.ExplorerRestartGeneration);
        Assert.Equal(0, stored.RestrictionsVersion);
    }

    [Fact]
    public async Task List_ShowsRestrictions()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        var pc = await AddPcAsync(app, "PC-01");
        await AddPcAsync(app, "PC-02");
        await SetAsync(admin, [pc.Id], On("windows-shutdown", "registry-editor"), On("Z", "A"), On("C"));

        var restricted = (await ListEntryAsync(admin, "PC-01")).GetProperty("restrictions");
        Assert.Equal(["registry-editor", "windows-shutdown"], Strings(restricted.GetProperty("policies")));
        Assert.Equal(["A", "Z"], Strings(restricted.GetProperty("hiddenDrives")));
        Assert.Equal(["C"], Strings(restricted.GetProperty("disabledDrives")));

        var other = (await ListEntryAsync(admin, "PC-02")).GetProperty("restrictions");
        Assert.Empty(Strings(other.GetProperty("policies")));
        Assert.Empty(Strings(other.GetProperty("hiddenDrives")));
        Assert.Empty(Strings(other.GetProperty("disabledDrives")));
    }

    private static string?[] Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()).ToArray();
}
