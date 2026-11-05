using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LabGuard.Server.Tests;

public class AccountEndpointTests
{
    private const string ValidPassword = "student pass 1";
    private const string TakenMessage = "That username is already taken.";

    private static readonly object TimeLimit60 = new { mode = "time-limit", minutes = 60 };
    private static readonly object OpenTime = new { mode = "open-time" };

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

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string username, string password = ValidPassword, object? defaultTime = null) =>
        client.PostAsJsonAsync("/api/accounts", new { username, password, defaultTime = defaultTime ?? TimeLimit60 });

    private static async Task<int> CreatedIdAsync(HttpClient client, string username, object? defaultTime = null)
    {
        var response = await CreateAsync(client, username, defaultTime: defaultTime);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonOf(response)).GetProperty("id").GetInt32();
    }

    private static Task<HttpResponseMessage> UpdateAsync(HttpClient client, int id, string username, string? password = null, object? defaultTime = null) =>
        client.PutAsJsonAsync($"/api/accounts/{id}", new { username, password, defaultTime = defaultTime ?? TimeLimit60 });

    private static Task<HttpResponseMessage> SendRawAsync(HttpClient client, HttpMethod method, string path, string body, string contentType = "application/json")
    {
        var request = new HttpRequestMessage(method, path) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        return client.SendAsync(request);
    }

    private static async Task AssertBadRequestAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await JsonOf(response)).GetProperty("error").GetString()));
    }

    // Authentication

    [Theory]
    [InlineData("GET", "/api/accounts")]
    [InlineData("POST", "/api/accounts")]
    [InlineData("PUT", "/api/accounts/1")]
    [InlineData("PATCH", "/api/accounts/1/status")]
    [InlineData("DELETE", "/api/accounts/1")]
    public async Task Endpoints_WithoutCookie_Return401(string method, string path)
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    // List

    [Fact]
    public async Task List_StartsEmpty()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await client.GetAsync("/api/accounts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await JsonOf(response)).GetArrayLength());
    }

    [Fact]
    public async Task List_IsOrderedByUsernameIgnoringCase()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        foreach (var name in new[] { "charlie", "Alice", "bob" })
        {
            await CreatedIdAsync(client, name);
        }

        var response = await client.GetAsync("/api/accounts");

        var names = (await JsonOf(response)).EnumerateArray().Select(a => a.GetProperty("username").GetString()!).ToArray();
        Assert.Equal(["Alice", "bob", "charlie"], names);
    }

    // Create

    [Fact]
    public async Task Create_WithTimeLimit_ReturnsShapeAndNeverExposesSecrets()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await CreateAsync(client, "  student.1  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ValidPassword, text);
        Assert.DoesNotContain("hash", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);

        var body = JsonDocument.Parse(text).RootElement;
        Assert.True(body.GetProperty("id").GetInt32() > 0);
        Assert.Equal("student.1", body.GetProperty("username").GetString());
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastLoginAt").ValueKind);
        var defaultTime = body.GetProperty("defaultTime");
        Assert.Equal("time-limit", defaultTime.GetProperty("mode").GetString());
        Assert.Equal(60, defaultTime.GetProperty("minutes").GetInt32());
    }

    [Fact]
    public async Task Create_WithOpenTime_OmitsMinutesKey()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await CreateAsync(client, "open", defaultTime: OpenTime);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var defaultTime = (await JsonOf(response)).GetProperty("defaultTime");
        Assert.Equal("open-time", defaultTime.GetProperty("mode").GetString());
        Assert.False(defaultTime.TryGetProperty("minutes", out _));
        var stored = await app.WithDbAsync(db => db.UserAccounts.SingleAsync());
        Assert.Null(stored.DefaultTimeMinutes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1440)]
    public async Task Create_WithBoundaryMinutes_Succeeds(int minutes)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await CreateAsync(client, "edge", defaultTime: new { mode = "time-limit", minutes });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_StoresHashedPasswordThatVerifies()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        await CreatedIdAsync(client, "hashed");

        var stored = await app.WithDbAsync(db => db.UserAccounts.SingleAsync());

        Assert.NotEqual(ValidPassword, stored.PasswordHash);
        Assert.DoesNotContain(ValidPassword, stored.PasswordHash);
        Assert.True(stored.IsActive);
        Assert.Null(stored.LastLoginUtc);
        var hasher = app.Services.GetRequiredService<IPasswordHasher<UserAccount>>();
        Assert.NotEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(stored, stored.PasswordHash, ValidPassword));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(stored, stored.PasswordHash, "something else"));
    }

    [Fact]
    public async Task Create_DoesNotTrimPassword()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        const string padded = "  padded pass  ";
        await CreateAsync(client, "padded", padded);

        var stored = await app.WithDbAsync(db => db.UserAccounts.SingleAsync());

        var hasher = app.Services.GetRequiredService<IPasswordHasher<UserAccount>>();
        Assert.NotEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(stored, stored.PasswordHash, padded));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(stored, stored.PasswordHash, padded.Trim()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("bad!char")]
    [InlineData("café")]
    [InlineData("semi;colon")]
    [InlineData("new\nline")]
    public async Task Create_WithInvalidUsername_Returns400(string username)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        await AssertBadRequestAsync(await CreateAsync(client, username));
        Assert.Equal(0, await app.WithDbAsync(db => db.UserAccounts.CountAsync()));
    }

    [Fact]
    public async Task Create_WithTooLongOrMissingUsername_Returns400()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        await AssertBadRequestAsync(await CreateAsync(client, new string('a', 65)));
        await AssertBadRequestAsync(await client.PostAsJsonAsync("/api/accounts", new { password = ValidPassword, defaultTime = TimeLimit60 }));
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, new string('a', 64))).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("1234567")]
    [InlineData("        ")]
    [InlineData("\t\t\t\t\t\t\t\t")]
    public async Task Create_WithInvalidPassword_Returns400(string password)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        await AssertBadRequestAsync(await CreateAsync(client, "someone", password));
    }

    [Fact]
    public async Task Create_WithTooLongOrMissingPassword_Returns400()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        await AssertBadRequestAsync(await CreateAsync(client, "someone", new string('x', 129)));
        await AssertBadRequestAsync(await client.PostAsJsonAsync("/api/accounts", new { username = "someone", defaultTime = TimeLimit60 }));
        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(client, "someone", new string('x', 128))).StatusCode);
    }

    public static TheoryData<object> InvalidDefaultTimes => new()
    {
        new { mode = "time-limit", minutes = 0 },
        new { mode = "time-limit", minutes = -5 },
        new { mode = "time-limit", minutes = 1441 },
        new { mode = "time-limit", minutes = 30.5 },
        new { mode = "time-limit", minutes = 1e12 },
        new { mode = "time-limit" },
        new { mode = "time-limit", minutes = (int?)null },
        new { mode = "unlimited" },
        new { mode = "Time-Limit", minutes = 60 },
        new { mode = "" },
        new { minutes = 60 },
        new { mode = "open-time", minutes = 60 },
        new { mode = "open-time", minutes = 0 },
        new { },
    };

    [Theory]
    [MemberData(nameof(InvalidDefaultTimes))]
    public async Task Create_WithInvalidDefaultTime_Returns400(object defaultTime)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        await AssertBadRequestAsync(await CreateAsync(client, "timed", defaultTime: defaultTime));
        Assert.Equal(0, await app.WithDbAsync(db => db.UserAccounts.CountAsync()));
    }

    [Fact]
    public async Task Create_WithMissingDefaultTime_Returns400()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        await AssertBadRequestAsync(await client.PostAsJsonAsync("/api/accounts", new { username = "timed", password = ValidPassword }));
        await AssertBadRequestAsync(await SendRawAsync(client, HttpMethod.Post, "/api/accounts", """{"username":"timed","password":"student pass 1","defaultTime":null}"""));
    }

    [Theory]
    [InlineData("""{"username":"x","password":"student pass 1","defaultTime":{"mode":"time-limit","minutes":99999999999999999999999999}}""")]
    [InlineData("""{"username":"x","password":"student pass 1","defaultTime":{"mode":"time-limit","minutes":1e999}}""")]
    [InlineData("""{"username":"x","password":"student pass 1","defaultTime":{"mode":"time-limit","minutes":true}}""")]
    [InlineData("""{"username":5,"password":"student pass 1","defaultTime":{"mode":"open-time"}}""")]
    public async Task Create_WithOverflowOrWrongJsonTypes_Returns400Not500(string body)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        await AssertBadRequestAsync(await SendRawAsync(client, HttpMethod.Post, "/api/accounts", body));
    }

    [Fact]
    public async Task Create_WithWrongContentType_Returns400InvalidRequest()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await SendRawAsync(client, HttpMethod.Post, "/api/accounts", "username=a&password=student pass 1", "text/plain");

        await AssertBadRequestAsync(response);
        Assert.Equal("Invalid request.", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    public async Task Create_WithMalformedOrNullJson_Returns400InvalidRequest(string body)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await SendRawAsync(client, HttpMethod.Post, "/api/accounts", body);

        await AssertBadRequestAsync(response);
        Assert.Equal("Invalid request.", (await JsonOf(response)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("ALICE")]
    [InlineData(" Alice ")]
    public async Task Create_WithTakenUsername_Returns409(string username)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        await CreatedIdAsync(client, "alice");

        var response = await CreateAsync(client, username);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(TakenMessage, (await JsonOf(response)).GetProperty("error").GetString());
        Assert.Equal(1, await app.WithDbAsync(db => db.UserAccounts.CountAsync()));
    }

    [Fact]
    public async Task Create_WithAdminUsername_Succeeds_BecauseTablesAreSeparate()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await CreateAsync(client, TestApp.Username);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // Update

    [Fact]
    public async Task Update_ChangesUsernameAndDefaultTime()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await CreatedIdAsync(client, "before");

        var response = await UpdateAsync(client, id, "after", defaultTime: OpenTime);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(id, body.GetProperty("id").GetInt32());
        Assert.Equal("after", body.GetProperty("username").GetString());
        Assert.Equal("open-time", body.GetProperty("defaultTime").GetProperty("mode").GetString());
        Assert.False(body.GetProperty("defaultTime").TryGetProperty("minutes", out _));
        var stored = await app.WithDbAsync(db => db.UserAccounts.SingleAsync());
        Assert.Equal("after", stored.Username);
        Assert.Null(stored.DefaultTimeMinutes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Update_WithNullOrEmptyPassword_KeepsHash(string? password)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await CreatedIdAsync(client, "keeper");
        var before = (await app.WithDbAsync(db => db.UserAccounts.SingleAsync())).PasswordHash;

        var response = await UpdateAsync(client, id, "keeper", password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, (await app.WithDbAsync(db => db.UserAccounts.SingleAsync())).PasswordHash);
    }

    [Fact]
    public async Task Update_WithNewPassword_ReplacesHash()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await CreatedIdAsync(client, "changer");
        const string newPassword = "a brand new pass";

        var response = await UpdateAsync(client, id, "changer", newPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("hash", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        var stored = await app.WithDbAsync(db => db.UserAccounts.SingleAsync());
        var hasher = app.Services.GetRequiredService<IPasswordHasher<UserAccount>>();
        Assert.NotEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(stored, stored.PasswordHash, newPassword));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(stored, stored.PasswordHash, ValidPassword));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("        ")]
    public async Task Update_WithInvalidPassword_Returns400AndChangesNothing(string password)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await CreatedIdAsync(client, "steady");
        var before = await app.WithDbAsync(db => db.UserAccounts.SingleAsync());

        await AssertBadRequestAsync(await UpdateAsync(client, id, "renamed", password));

        var after = await app.WithDbAsync(db => db.UserAccounts.SingleAsync());
        Assert.Equal("steady", after.Username);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
    }

    [Fact]
    public async Task Update_WithInvalidUsernameOrDefaultTime_Returns400()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await CreatedIdAsync(client, "valid");

        await AssertBadRequestAsync(await UpdateAsync(client, id, "bad name"));
        await AssertBadRequestAsync(await UpdateAsync(client, id, "valid", defaultTime: new { mode = "time-limit", minutes = 1441 }));
        await AssertBadRequestAsync(await UpdateAsync(client, id, "valid", defaultTime: new { mode = "open-time", minutes = 5 }));
        await AssertBadRequestAsync(await client.PutAsJsonAsync($"/api/accounts/{id}", new { username = "valid" }));
        await AssertBadRequestAsync(await SendRawAsync(client, HttpMethod.Put, $"/api/accounts/{id}", "{ not json"));
        await AssertBadRequestAsync(await SendRawAsync(client, HttpMethod.Put, $"/api/accounts/{id}", "{}", "text/plain"));
    }

    [Fact]
    public async Task Update_MissingAccount_Returns404()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await UpdateAsync(client, 999, "ghost");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_OntoAnotherAccountsUsername_Returns409_EvenWithDifferentCase()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        await CreatedIdAsync(client, "first");
        var secondId = await CreatedIdAsync(client, "second");

        var exact = await UpdateAsync(client, secondId, "first");
        var differentCase = await UpdateAsync(client, secondId, "FIRST");

        Assert.Equal(HttpStatusCode.Conflict, exact.StatusCode);
        Assert.Equal(TakenMessage, (await JsonOf(exact)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, differentCase.StatusCode);
        var names = (await app.WithDbAsync(db => db.UserAccounts.OrderBy(u => u.Id).ToListAsync())).Select(u => u.Username);
        Assert.Equal(["first", "second"], names);
    }

    [Fact]
    public async Task Update_CaseOnlyRename_Succeeds()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await CreatedIdAsync(client, "casey");

        var response = await UpdateAsync(client, id, "CASEY");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("CASEY", (await JsonOf(response)).GetProperty("username").GetString());
        Assert.Equal("CASEY", (await app.WithDbAsync(db => db.UserAccounts.SingleAsync())).Username);
    }

    [Fact]
    public async Task Update_KeepingSameUsername_Succeeds()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await CreatedIdAsync(client, "same");

        var response = await UpdateAsync(client, id, "same", defaultTime: new { mode = "time-limit", minutes = 15 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Status

    [Fact]
    public async Task Patch_TogglesActiveFlag()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await CreatedIdAsync(client, "toggle");

        var off = await client.PatchAsJsonAsync($"/api/accounts/{id}/status", new { isActive = false });
        var offStored = await app.WithDbAsync(db => db.UserAccounts.SingleAsync());
        var on = await client.PatchAsJsonAsync($"/api/accounts/{id}/status", new { isActive = true });

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await JsonOf(off)).GetProperty("isActive").GetBoolean());
        Assert.False(offStored.IsActive);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True((await JsonOf(on)).GetProperty("isActive").GetBoolean());
        Assert.True((await app.WithDbAsync(db => db.UserAccounts.SingleAsync())).IsActive);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"isActive":null}""")]
    [InlineData("""{"isActive":"yes"}""")]
    [InlineData("{ not json")]
    [InlineData("null")]
    public async Task Patch_WithoutValidIsActive_Returns400(string body)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await CreatedIdAsync(client, "toggle");

        await AssertBadRequestAsync(await SendRawAsync(client, HttpMethod.Patch, $"/api/accounts/{id}/status", body));
        Assert.True((await app.WithDbAsync(db => db.UserAccounts.SingleAsync())).IsActive);
    }

    [Fact]
    public async Task Patch_WithWrongContentType_Returns400()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await CreatedIdAsync(client, "toggle");

        await AssertBadRequestAsync(await SendRawAsync(client, HttpMethod.Patch, $"/api/accounts/{id}/status", "isActive=false", "text/plain"));
    }

    [Fact]
    public async Task Patch_MissingAccount_Returns404()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await client.PatchAsJsonAsync("/api/accounts/999/status", new { isActive = false });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Delete

    [Fact]
    public async Task Delete_RemovesOnlyThatAccount()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await CreatedIdAsync(client, "doomed");
        await CreatedIdAsync(client, "survivor");

        var response = await client.DeleteAsync($"/api/accounts/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var remaining = await app.WithDbAsync(db => db.UserAccounts.ToListAsync());
        Assert.Equal("survivor", Assert.Single(remaining).Username);
    }

    [Fact]
    public async Task Delete_MissingAccountOrSecondDelete_Returns404()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var id = await CreatedIdAsync(client, "once");

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/accounts/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/accounts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/accounts/999")).StatusCode);
    }

    [Fact]
    public async Task NonIntegerId_DoesNotMatchRoute()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await client.DeleteAsync("/api/accounts/abc");

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }

    // Admin account is separate

    [Fact]
    public async Task AccountChanges_DoNotAffectAdminAccountOrLogin()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        var adminBefore = await app.WithDbAsync(db => db.AdminAccounts.SingleAsync());
        var id = await CreatedIdAsync(client, "pupil");
        await UpdateAsync(client, id, "pupil2", "another pass 1");
        await client.PatchAsJsonAsync($"/api/accounts/{id}/status", new { isActive = false });
        await client.DeleteAsync($"/api/accounts/{id}");

        using var fresh = app.CreateBrowser();
        var login = await fresh.PostAsJsonAsync("/api/auth/login", new { username = TestApp.Username, password = TestApp.Password });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var adminAfter = await app.WithDbAsync(db => db.AdminAccounts.SingleAsync());
        Assert.Equal(adminBefore.Id, adminAfter.Id);
        Assert.Equal(adminBefore.Username, adminAfter.Username);
        Assert.Equal(adminBefore.PasswordHash, adminAfter.PasswordHash);
        Assert.Equal(adminBefore.SessionVersion, adminAfter.SessionVersion);
    }

    [Fact]
    public async Task Startup_DoesNotSeedUserAccounts()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();
        await client.GetAsync("/api/health");

        Assert.Equal(0, await app.WithDbAsync(db => db.UserAccounts.CountAsync()));
    }
}
