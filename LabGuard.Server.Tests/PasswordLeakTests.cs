using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Accounts;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Tests;

// Replies must never carry a password, a stored hash, or a property named like a password.
public class PasswordLeakTests
{
    private const string Password = "lantern violet 42";
    private const string NewPassword = "copper harbor 77";

    private static readonly string[] KnownPasswords = [Password, NewPassword, TestApp.Password];

    private static async Task<HttpClient> SignedInAsync(TestApp app)
    {
        var client = app.CreateBrowser();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = TestApp.Username, password = TestApp.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<TestPcToken> AddPcAsync(TestApp app)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var now = app.Time.GetUtcNow();
        var id = await app.WithDbAsync(async db =>
        {
            var pc = new Pc { Name = "PC-01", TokenHash = hash, RegisteredAtUtc = now, LastSeenUtc = now };
            db.Pcs.Add(pc);
            await db.SaveChangesAsync();
            return pc.Id;
        });
        return new TestPcToken(id, token);
    }

    private sealed record TestPcToken(int Id, string Token);

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    // Every password the test uses, plus the stored hash of every account, admin included.
    private static async Task<string[]> SecretsAsync(TestApp app)
    {
        var userHashes = await app.WithDbAsync(db => db.UserAccounts.AsNoTracking().Select(u => u.PasswordHash).ToListAsync());
        var adminHashes = await app.WithDbAsync(db => db.AdminAccounts.AsNoTracking().Select(a => a.PasswordHash).ToListAsync());
        return [.. KnownPasswords, .. userHashes, .. adminHashes];
    }

    // Checks the raw body, then the parsed JSON: every property name and every string value. The parsed check
    // catches an escaped form (such as + for a plus sign in a base64 hash) that the raw text check would miss.
    private static async Task<string> AssertNoSecretsAsync(HttpResponseMessage response, string[] secrets)
    {
        var body = await response.Content.ReadAsStringAsync();
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, body);
        }

        if (body.Length == 0)
        {
            return body;
        }

        using var json = JsonDocument.Parse(body);
        var names = new List<string>();
        var values = new List<string>();
        CollectNamesAndValues(json.RootElement, names, values);
        foreach (var name in names)
        {
            Assert.DoesNotContain("password", name, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var text in names.Concat(values))
        {
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, text);
            }
        }

        return body;
    }

    private static void CollectNamesAndValues(JsonElement element, List<string> names, List<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    names.Add(property.Name);
                    CollectNamesAndValues(property.Value, names, values);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectNamesAndValues(item, names, values);
                }

                break;
            case JsonValueKind.String:
                values.Add(element.GetString()!);
                break;
        }
    }

    private static object AccountBody(string username, string password) =>
        new { username, password, defaultTime = new { mode = AccountEndpoints.TimeLimitMode, minutes = 60 } };

    [Fact]
    public async Task NoReply_CarriesAPasswordOrAPasswordHash()
    {
        using var app = new TestApp();
        using var admin = app.CreateBrowser();
        var signIn = await admin.PostAsJsonAsync("/api/auth/login", new { username = TestApp.Username, password = TestApp.Password });
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        await AssertNoSecretsAsync(signIn, await SecretsAsync(app));

        var session = await admin.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        await AssertNoSecretsAsync(session, await SecretsAsync(app));

        var created = await admin.PostAsJsonAsync("/api/accounts", AccountBody("alice", Password));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await AssertNoSecretsAsync(created, await SecretsAsync(app));
        var aliceId = JsonDocument.Parse(createdBody).RootElement.GetProperty("id").GetInt32();

        var listed = await admin.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        await AssertNoSecretsAsync(listed, await SecretsAsync(app));

        var updated = await admin.PutAsJsonAsync($"/api/accounts/{aliceId}", AccountBody("alice", NewPassword));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        await AssertNoSecretsAsync(updated, await SecretsAsync(app));

        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/client/login")
        {
            Content = JsonContent.Create(new { username = "alice", password = NewPassword }),
        };
        loginRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {pc.Token}");
        var pcLogin = await pcClient.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, pcLogin.StatusCode);
        await AssertNoSecretsAsync(pcLogin, await SecretsAsync(app));

        var pcList = await admin.GetAsync("/api/pcs");
        Assert.Equal(HttpStatusCode.OK, pcList.StatusCode);
        var pcListBody = await AssertNoSecretsAsync(pcList, await SecretsAsync(app));
        Assert.Contains("alice", pcListBody);

        using var heartbeatRequest = new HttpRequestMessage(HttpMethod.Post, "/api/client/heartbeat");
        heartbeatRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {pc.Token}");
        var heartbeat = await pcClient.SendAsync(heartbeatRequest);
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        await AssertNoSecretsAsync(heartbeat, await SecretsAsync(app));
    }

    [Fact]
    public async Task ErrorReplies_CarryNoPasswordOrHash_AndNeitherDoTheLogs()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/accounts", AccountBody("alice", Password))).StatusCode);

        using var stranger = app.CreateBrowser();
        var wrongAdmin = await stranger.PostAsJsonAsync("/api/auth/login", new { username = TestApp.Username, password = NewPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongAdmin.StatusCode);
        await AssertNoPasswordValuesAsync(wrongAdmin, await SecretsAsync(app));

        var tooShort = await admin.PostAsJsonAsync("/api/accounts", AccountBody("bob", "short"));
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        await AssertNoPasswordValuesAsync(tooShort, [.. await SecretsAsync(app), "short"]);

        var wrongCurrent = await admin.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = NewPassword, newPassword = Password });
        Assert.False(wrongCurrent.IsSuccessStatusCode);
        await AssertNoPasswordValuesAsync(wrongCurrent, await SecretsAsync(app));

        using var pcClient = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        using var wrongPcLogin = new HttpRequestMessage(HttpMethod.Post, "/api/client/login")
        {
            Content = JsonContent.Create(new { username = "alice", password = NewPassword }),
        };
        wrongPcLogin.Headers.TryAddWithoutValidation("Authorization", $"Bearer {pc.Token}");
        var refused = await pcClient.SendAsync(wrongPcLogin);
        Assert.False(refused.IsSuccessStatusCode);
        await AssertNoPasswordValuesAsync(refused, await SecretsAsync(app));

        var secrets = await SecretsAsync(app);
        foreach (var entry in app.Logs.Entries)
        {
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, entry);
            }
        }
    }

    // An error message may use the word "password", so this checks only for the secret values themselves.
    private static async Task AssertNoPasswordValuesAsync(HttpResponseMessage response, string[] secrets)
    {
        var body = await response.Content.ReadAsStringAsync();
        var text = body.Length == 0 ? "" : JsonDocument.Parse(body).RootElement.ToString();
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, body);
            Assert.DoesNotContain(secret, text);
        }
    }

    [Fact]
    public async Task StoredHash_IsNotThePassword_AndTwoAccountsWithOnePasswordGetDifferentHashes()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);

        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/accounts", AccountBody("alice", Password))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/accounts", AccountBody("bob", Password))).StatusCode);

        var hashes = await app.WithDbAsync(db => db.UserAccounts.AsNoTracking().OrderBy(u => u.Id).Select(u => u.PasswordHash).ToListAsync());
        Assert.Equal(2, hashes.Count);
        foreach (var hash in hashes)
        {
            Assert.NotEqual(Password, hash);
            Assert.DoesNotContain(Password, hash);
            Assert.NotEmpty(hash);
        }

        Assert.NotEqual(hashes[0], hashes[1]);
    }
}
