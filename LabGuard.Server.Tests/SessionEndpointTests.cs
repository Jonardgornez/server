using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using LabGuard.Server.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LabGuard.Server.Tests;

public class SessionEndpointTests
{
    private const string StudentPassword = "student pass 1";
    private const string WrongCredentialsMessage = "Wrong username or password.";
    private const string NotRegisteredMessage = "This PC isn't registered.";

    private sealed record TestPc(int Id, string Token);

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

    private static async Task<TestPc> AddPcAsync(TestApp app, string name = "PC-01")
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var now = app.Time.GetUtcNow();
        var id = await app.WithDbAsync(async db =>
        {
            var pc = new Pc { Name = name, TokenHash = hash, RegisteredAtUtc = now, LastSeenUtc = now };
            db.Pcs.Add(pc);
            await db.SaveChangesAsync();
            return pc.Id;
        });
        return new TestPc(id, token);
    }

    private static async Task<int> AddAccountAsync(
        TestApp app,
        string username = "student",
        string password = StudentPassword,
        int? minutes = 60,
        bool isActive = true)
    {
        var hasher = app.Services.GetRequiredService<IPasswordHasher<UserAccount>>();
        return await app.WithDbAsync(async db =>
        {
            var account = new UserAccount { Username = username, IsActive = isActive, DefaultTimeMinutes = minutes };
            account.PasswordHash = hasher.HashPassword(account, password);
            db.UserAccounts.Add(account);
            await db.SaveChangesAsync();
            return account.Id;
        });
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string? token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, TestPc pc, string username = "student", string password = StudentPassword) =>
        PostAsync(client, "/api/client/login", pc.Token, JsonContent.Create(new { username, password }));

    private static Task<HttpResponseMessage> LogoutAsync(HttpClient client, TestPc pc) =>
        PostAsync(client, "/api/client/logout", pc.Token);

    private static Task<HttpResponseMessage> HeartbeatAsync(HttpClient client, TestPc pc, int? sessionId = null) =>
        PostAsync(client, "/api/client/heartbeat", pc.Token, sessionId is null ? null : JsonContent.Create(new { sessionId }));

    private static async Task<int> LoggedInSessionIdAsync(HttpClient client, TestPc pc, string username = "student", string password = StudentPassword)
    {
        var response = await LoginAsync(client, pc, username, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonOf(response)).GetProperty("sessionId").GetInt32();
    }

    private static Task<PcSession> StoredSessionAsync(TestApp app, int id) =>
        app.WithDbAsync(db => db.PcSessions.AsNoTracking().SingleAsync(s => s.Id == id));

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string message)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(message, (await JsonOf(response)).GetProperty("error").GetString());
    }

    // Login: success

    [Fact]
    public async Task Login_WithTimeLimit_ReturnsSessionAndStoresIt()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app, "PC-07");
        var accountId = await AddAccountAsync(app, "Student", minutes: 60);

        var response = await LoginAsync(client, pc, "  STUDENT  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(
            new[] { "secondsRemaining", "sessionId", "time", "username" },
            body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("Student", body.GetProperty("username").GetString());
        Assert.Equal("time-limit", body.GetProperty("time").GetProperty("mode").GetString());
        Assert.Equal(60, body.GetProperty("time").GetProperty("minutes").GetInt32());
        Assert.Equal(3600, body.GetProperty("secondsRemaining").GetInt32());

        var session = await StoredSessionAsync(app, body.GetProperty("sessionId").GetInt32());
        Assert.Equal(pc.Id, session.PcId);
        Assert.Equal("PC-07", session.PcName);
        Assert.Equal(accountId, session.UserAccountId);
        Assert.Equal("Student", session.Username);
        Assert.Equal(60, session.DefaultTimeMinutes);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), session.StartedAtUtc.ToUnixTimeMilliseconds());
        Assert.Equal(app.Time.GetUtcNow().AddMinutes(60).ToUnixTimeMilliseconds(), session.ExpiresAtUtc!.Value.ToUnixTimeMilliseconds());
        Assert.Null(session.EndedAtUtc);
        Assert.Null(session.EndReason);
    }

    [Fact]
    public async Task Login_WithOpenTime_HasNoMinutesAndNullSecondsRemaining()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: null);

        var response = await LoginAsync(client, pc);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        var time = body.GetProperty("time");
        Assert.Equal("open-time", time.GetProperty("mode").GetString());
        Assert.Single(time.EnumerateObject());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("secondsRemaining").ValueKind);
        var session = await StoredSessionAsync(app, body.GetProperty("sessionId").GetInt32());
        Assert.Null(session.ExpiresAtUtc);
        Assert.Null(session.DefaultTimeMinutes);
    }

    [Fact]
    public async Task Login_SetsLastLoginAndNeverReturnsOrLogsThePassword()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var accountId = await AddAccountAsync(app);

        var response = await LoginAsync(client, pc);

        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(StudentPassword, text);
        Assert.DoesNotContain("hash", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Contains(StudentPassword) || entry.Contains(pc.Token));
        var stored = await app.WithDbAsync(db => db.UserAccounts.AsNoTracking().SingleAsync(u => u.Id == accountId));
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.LastLoginUtc!.Value.ToUnixTimeMilliseconds());
    }

    // Login: failures

    [Fact]
    public async Task Login_WrongPasswordAndUnknownUser_GiveIdentical403Responses()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);

        var wrongPassword = await LoginAsync(client, pc, password: "not the password");
        var unknownUser = await LoginAsync(client, pc, "nobody", "not the password");

        Assert.Equal(HttpStatusCode.Forbidden, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unknownUser.StatusCode);
        var wrongBody = await wrongPassword.Content.ReadAsStringAsync();
        Assert.Equal(wrongBody, await unknownUser.Content.ReadAsStringAsync());
        Assert.Equal(WrongCredentialsMessage, (await JsonOf(wrongPassword)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Login_DisabledAccount_WithCorrectPassword_Returns403DisabledAndDoesNotCountAsFailure()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, isActive: false);

        for (var i = 0; i < ClientEndpoints.MaxFailedLogins + 1; i++)
        {
            await AssertErrorAsync(await LoginAsync(client, pc), HttpStatusCode.Forbidden, "This account is disabled.");
        }

        Assert.Equal(0, (await app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync())).FailedLoginCount);
        Assert.Equal(0, await app.WithDbAsync(db => db.PcSessions.CountAsync()));
    }

    [Fact]
    public async Task Login_DisabledAccount_WithWrongPassword_LooksLikeAnyWrongLogin()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, isActive: false);

        var response = await LoginAsync(client, pc, password: "not the password");

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, WrongCredentialsMessage);
        Assert.Equal(1, (await app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync())).FailedLoginCount);
    }

    [Fact]
    public async Task Login_WithOverLongValues_Returns403LikeAnyWrongLoginAndCountsAsFailure()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);

        await AssertErrorAsync(await LoginAsync(client, pc, new string('a', 65)), HttpStatusCode.Forbidden, WrongCredentialsMessage);
        await AssertErrorAsync(await LoginAsync(client, pc, "student", new string('a', 129)), HttpStatusCode.Forbidden, WrongCredentialsMessage);

        Assert.Equal(2, (await app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync())).FailedLoginCount);
    }

    [Theory]
    [InlineData("{}", "application/json")]
    [InlineData("""{"username":"","password":"x"}""", "application/json")]
    [InlineData("""{"username":"   ","password":"x"}""", "application/json")]
    [InlineData("""{"username":"student","password":"   "}""", "application/json")]
    [InlineData("""{"username":"student"}""", "application/json")]
    [InlineData("""{"username":5,"password":"x"}""", "application/json")]
    [InlineData("{not json", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("", "application/json")]
    [InlineData("""{"username":"student","password":"student pass 1"}""", "text/plain")]
    public async Task Login_WithBlankOrMalformedBody_Returns400AndDoesNotCountAsFailure(string body, string contentType)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);

        var response = await PostAsync(client, "/api/client/login", pc.Token, new StringContent(body, Encoding.UTF8, contentType));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "Enter a username and password.");
        Assert.Equal(0, (await app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync())).FailedLoginCount);
    }

    [Fact]
    public async Task Login_WithoutBody_Returns400()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);

        await AssertErrorAsync(await PostAsync(client, "/api/client/login", pc.Token), HttpStatusCode.BadRequest, "Enter a username and password.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer not-a-registered-token")]
    public async Task LoginAndLogout_WithMissingOrUnknownToken_Return401(string? authorization)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        await AddAccountAsync(app);

        foreach (var path in new[] { "/api/client/login", "/api/client/logout" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { username = "student", password = StudentPassword }) };
            if (authorization is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            await AssertErrorAsync(await client.SendAsync(request), HttpStatusCode.Unauthorized, NotRegisteredMessage);
        }

        Assert.Equal(0, await app.WithDbAsync(db => db.PcSessions.CountAsync()));
    }

    [Fact]
    public async Task Login_WithWrongCredentials_NeverReturns401()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 7; i++)
        {
            statuses.Add((await LoginAsync(client, pc, password: "wrong")).StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.Unauthorized, statuses);
    }

    // Login: lockout

    [Fact]
    public async Task Login_FifthFailure_Returns429WithRetrySeconds_AndLocksBeforeVerifying()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        for (var i = 0; i < ClientEndpoints.MaxFailedLogins - 1; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await LoginAsync(client, pc, password: "wrong")).StatusCode);
        }

        var fifth = await LoginAsync(client, pc, password: "wrong");

        await AssertErrorAsync(fifth, HttpStatusCode.TooManyRequests, "Too many attempts. Try again in 30 seconds.");
        Assert.Equal(30, (await JsonOf(fifth)).GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal(TimeSpan.FromSeconds(30), fifth.Headers.RetryAfter!.Delta);

        // The correct password is refused while locked.
        app.Time.Advance(TimeSpan.FromSeconds(10));
        var locked = await LoginAsync(client, pc);
        await AssertErrorAsync(locked, HttpStatusCode.TooManyRequests, "Too many attempts. Try again in 20 seconds.");
        Assert.Equal(0, await app.WithDbAsync(db => db.PcSessions.CountAsync()));
    }

    [Fact]
    public async Task Login_UnlocksAfter30Seconds()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        for (var i = 0; i < ClientEndpoints.MaxFailedLogins; i++)
        {
            await LoginAsync(client, pc, password: "wrong");
        }

        app.Time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, pc)).StatusCode);
        app.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, pc)).StatusCode);
        var stored = await app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync());
        Assert.Equal(0, stored.FailedLoginCount);
        // A success no longer erases a lock; the expired one is simply in the past.
        Assert.True(stored.LoginLockedUntilUtc <= app.Time.GetUtcNow());
    }

    [Fact]
    public async Task Login_AfterALockExpires_FailuresStartCountingFromZero()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        for (var i = 0; i < ClientEndpoints.MaxFailedLogins; i++)
        {
            await LoginAsync(client, pc, password: "wrong");
        }

        app.Time.Advance(TimeSpan.FromSeconds(30));

        for (var i = 0; i < ClientEndpoints.MaxFailedLogins - 1; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await LoginAsync(client, pc, password: "wrong")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, pc, password: "wrong")).StatusCode);
    }

    [Fact]
    public async Task Login_Success_DoesNotResetEarlierFailures_SoTheNextWrongGuessLocks()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        for (var i = 0; i < ClientEndpoints.MaxFailedLogins - 1; i++)
        {
            await LoginAsync(client, pc, password: "wrong");
        }

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, pc)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, pc, password: "wrong")).StatusCode);
    }

    [Fact]
    public async Task Login_Success_LeavesTheFailureCountUnchanged()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        await LoginAsync(client, pc, password: "wrong");
        await LoginAsync(client, pc, password: "wrong");

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, pc)).StatusCode);

        Assert.Equal(2, (await StoredPcAsync(app)).FailedLoginCount);
    }

    [Fact]
    public async Task Login_SuccessOnTheAttemptThatCausedTheLock_SucceedsAndLeavesFourFailuresUnlocked()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        for (var i = 0; i < ClientEndpoints.MaxFailedLogins - 1; i++)
        {
            await LoginAsync(client, pc, password: "wrong");
        }

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, pc)).StatusCode);

        var stored = await StoredPcAsync(app);
        Assert.Null(stored.LoginLockedUntilUtc);
        Assert.Equal(ClientEndpoints.MaxFailedLogins - 1, stored.FailedLoginCount);
    }

    [Fact]
    public async Task Login_FourWrongOneValidOneWrong_LocksThePc()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);

        var statuses = await RunAttackCycleAsync(client, pc);

        Assert.Equal(
            new[] { HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests },
            statuses);
    }

    [Fact]
    public async Task Login_MixingInValidLogins_NeverAllowsMoreThanFiveWrongGuessesPerLockWindow()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);

        for (var window = 0; window < 2; window++)
        {
            var statuses = await RunAttackCycleAsync(client, pc);

            // Four wrong guesses were answered, the fifth hit the lock, and the valid login after it was refused too.
            Assert.Equal(HttpStatusCode.TooManyRequests, statuses[1]);
            Assert.Equal(HttpStatusCode.TooManyRequests, statuses[2]);
            app.Time.Advance(ClientEndpoints.LoginLockDuration);
        }
    }

    // Four wrong guesses, a valid login, a wrong guess, then a valid login; returns the last three statuses.
    private static async Task<HttpStatusCode[]> RunAttackCycleAsync(HttpClient client, TestPc pc)
    {
        for (var i = 0; i < ClientEndpoints.MaxFailedLogins - 1; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await LoginAsync(client, pc, password: "wrong")).StatusCode);
        }

        var valid = (await LoginAsync(client, pc)).StatusCode;
        var wrong = (await LoginAsync(client, pc, password: "wrong")).StatusCode;
        var validWhileLocked = (await LoginAsync(client, pc)).StatusCode;
        return [valid, wrong, validWhileLocked];
    }

    [Fact]
    public async Task Login_ValidLoginInFlight_DoesNotClearALockSetByOthers()
    {
        var hasher = new BlockingHasher();
        using var app = new TestApp(configureServices: services => UseHasher(services, hasher));
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);

        // The valid login reserves its attempt, then waits inside the hash.
        var validLogin = Task.Run(() => LoginAsync(client, pc));
        Assert.True(await hasher.WaitUntilBlockedAsync());

        // Four more attempts: the last is the fifth reservation, so it sets the lock.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < ClientEndpoints.MaxFailedLogins - 1; i++)
        {
            statuses.Add((await LoginAsync(client, pc, password: "wrong")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        hasher.Release();
        await validLogin;

        var stored = await StoredPcAsync(app);
        Assert.True(stored.LoginLockedUntilUtc > app.Time.GetUtcNow());
        Assert.Equal(0, stored.FailedLoginCount);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, pc, password: "wrong")).StatusCode);
    }

    [Fact]
    public async Task Login_GiveBack_NeverGoesBelowZero()
    {
        var hasher = new BlockingHasher();
        using var app = new TestApp(configureServices: services => UseHasher(services, hasher));
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var validLogin = Task.Run(() => LoginAsync(client, pc));
        Assert.True(await hasher.WaitUntilBlockedAsync());

        // Something else already took the count back to zero while the login was hashing.
        await app.WithDbAsync(db => db.Pcs.ExecuteUpdateAsync(setters => setters.SetProperty(p => p.FailedLoginCount, 0)));
        hasher.Release();

        Assert.Equal(HttpStatusCode.OK, (await validLogin).StatusCode);
        Assert.Equal(0, (await StoredPcAsync(app)).FailedLoginCount);
    }

    [Fact]
    public async Task Login_DisabledAccount_GiveBackNeverGoesBelowZero()
    {
        var hasher = new BlockingHasher();
        using var app = new TestApp(configureServices: services => UseHasher(services, hasher));
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, isActive: false);
        var disabledLogin = Task.Run(() => LoginAsync(client, pc));
        Assert.True(await hasher.WaitUntilBlockedAsync());

        await app.WithDbAsync(db => db.Pcs.ExecuteUpdateAsync(setters => setters.SetProperty(p => p.FailedLoginCount, 0)));
        hasher.Release();

        Assert.Equal(HttpStatusCode.Forbidden, (await disabledLogin).StatusCode);
        Assert.Equal(0, (await StoredPcAsync(app)).FailedLoginCount);
    }

    [Fact]
    public async Task Login_ErrorAfterReserving_GivesTheAttemptBackAndStillFails()
    {
        var hasher = new ThrowingHasher();
        using var app = new TestApp(configureServices: services => UseHasher(services, hasher));
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        await LoginAsync(client, pc, password: "wrong");

        var response = await LoginAsync(client, pc, password: ThrowingHasher.Trigger);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, (await StoredPcAsync(app)).FailedLoginCount);
    }

    private static Task<Pc> StoredPcAsync(TestApp app) =>
        app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync());

    private static void UseHasher(IServiceCollection services, IPasswordHasher<UserAccount> hasher)
    {
        services.RemoveAll<IPasswordHasher<UserAccount>>();
        services.AddSingleton(hasher);
    }

    [Fact]
    public async Task Login_LockoutOnOnePc_DoesNotAffectAnother()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient("192.168.1.50");
        using var otherClient = app.CreatePcClient("192.168.1.51");
        var locked = await AddPcAsync(app, "PC-01");
        var other = await AddPcAsync(app, "PC-02");
        await AddAccountAsync(app);
        for (var i = 0; i < ClientEndpoints.MaxFailedLogins; i++)
        {
            await LoginAsync(client, locked, password: "wrong");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, locked)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(otherClient, other)).StatusCode);
    }

    [Fact]
    public async Task Login_ParallelGuesses_CannotSlipPastTheCounter()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => LoginAsync(client, pc, password: "wrong")));

        Assert.Equal(ClientEndpoints.MaxFailedLogins - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Forbidden));
        Assert.Equal(10 - (ClientEndpoints.MaxFailedLogins - 1), responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task Login_FailuresOlderThanTenMinutes_AreForgotten()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        for (var i = 0; i < ClientEndpoints.MaxFailedLogins - 1; i++)
        {
            await LoginAsync(client, pc, password: "wrong");
        }

        app.Time.Advance(ClientEndpoints.FailureMemory + TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.Forbidden, (await LoginAsync(client, pc, password: "wrong")).StatusCode);
        Assert.Equal(1, (await app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync())).FailedLoginCount);
        for (var i = 0; i < ClientEndpoints.MaxFailedLogins - 2; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await LoginAsync(client, pc, password: "wrong")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, pc, password: "wrong")).StatusCode);
    }

    [Fact]
    public async Task Login_FailuresWithinTenMinutes_StillLock()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        for (var i = 0; i < ClientEndpoints.MaxFailedLogins - 1; i++)
        {
            await LoginAsync(client, pc, password: "wrong");
        }

        app.Time.Advance(ClientEndpoints.FailureMemory - TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, pc, password: "wrong")).StatusCode);
    }

    [Fact]
    public async Task Login_RealAndDummyPaths_VerifyExactlyOnceWithTheRegisteredHasher()
    {
        var hasher = new RecordingHasher();
        using var app = new TestApp(configureServices: services =>
        {
            services.RemoveAll<IPasswordHasher<UserAccount>>();
            services.AddSingleton<IPasswordHasher<UserAccount>>(hasher);
        });
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var realHash = await app.WithDbAsync(db => db.UserAccounts.AsNoTracking().Select(u => u.PasswordHash).SingleAsync());

        await LoginAsync(client, pc, password: "wrong");
        Assert.Equal(new[] { realHash }, hasher.Verified.ToArray());
        hasher.Verified.Clear();

        await LoginAsync(client, pc, "nobody", "wrong");
        await LoginAsync(client, pc, new string('a', 65), "wrong");
        var dummyHashes = hasher.Verified.ToArray();

        // One verification per attempt, and the dummy hash was produced by the very hasher that verifies it.
        Assert.Equal(2, dummyHashes.Length);
        Assert.All(dummyHashes, hash =>
        {
            Assert.NotEqual(realHash, hash);
            Assert.Contains(hash, hasher.Produced);
        });

        hasher.Verified.Clear();
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, pc)).StatusCode);
        Assert.Equal(new[] { realHash }, hasher.Verified.ToArray());
    }

    [Fact]
    public async Task Database_UsesWal_AndAWriteWaitsForABriefLock()
    {
        using var app = new TestApp();
        var pc = await AddPcAsync(app);
        var connectionString = await app.WithDbAsync(db => Task.FromResult(db.Database.GetConnectionString()!));

        await using var blocker = new SqliteConnection(connectionString);
        await blocker.OpenAsync();
        await using var mode = blocker.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", Convert.ToString(await mode.ExecuteScalarAsync()));

        // Another writer holds the lock for half a second; the write must wait for it, not fail.
        await using var begin = blocker.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        await begin.ExecuteNonQueryAsync();
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            await using var commit = blocker.CreateCommand();
            commit.CommandText = "COMMIT;";
            await commit.ExecuteNonQueryAsync();
        });

        var changed = await app.WithDbAsync(db => db.Pcs
            .Where(p => p.Id == pc.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.IpAddress, "192.168.1.9")));
        await release;

        Assert.Equal(1, changed);
    }

    private sealed class RecordingHasher : IPasswordHasher<UserAccount>
    {
        private readonly PasswordHasher<UserAccount> _inner = new();

        public ConcurrentQueue<string> Produced { get; } = new();
        public ConcurrentQueue<string> Verified { get; } = new();

        public string HashPassword(UserAccount user, string password)
        {
            var hash = _inner.HashPassword(user, password);
            Produced.Enqueue(hash);
            return hash;
        }

        public PasswordVerificationResult VerifyHashedPassword(UserAccount user, string hashedPassword, string providedPassword)
        {
            Verified.Enqueue(hashedPassword);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    // Holds the first verification of the valid password until released, so overlapping logins are deterministic.
    private sealed class BlockingHasher : IPasswordHasher<UserAccount>
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
        private readonly PasswordHasher<UserAccount> _inner = new();
        private readonly SemaphoreSlim _blocked = new(0);
        private readonly ManualResetEventSlim _release = new(false);

        public string HashPassword(UserAccount user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(UserAccount user, string hashedPassword, string providedPassword)
        {
            if (providedPassword == StudentPassword && !_release.IsSet)
            {
                _blocked.Release();
                _release.Wait(Patience);
            }

            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }

        public Task<bool> WaitUntilBlockedAsync() => _blocked.WaitAsync(Patience);

        public void Release() => _release.Set();
    }

    private sealed class ThrowingHasher : IPasswordHasher<UserAccount>
    {
        public const string Trigger = "make it throw";

        private readonly PasswordHasher<UserAccount> _inner = new();

        public string HashPassword(UserAccount user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(UserAccount user, string hashedPassword, string providedPassword) =>
            providedPassword == Trigger
                ? throw new InvalidOperationException("Simulated hasher failure.")
                : _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }

    [Fact]
    public async Task Login_AfterTheRateLimit_Returns429WithRetryAfterBody_ForThatAddressOnly()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient("192.168.1.50");
        using var otherClient = app.CreatePcClient("192.168.1.51");

        for (var i = 0; i < ClientEndpoints.MaxLoginsPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, "/api/client/login", "x")).StatusCode);
        }

        var limited = await PostAsync(client, "/api/client/login", "x");

        await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, "Too many attempts. Try again in a minute.");
        Assert.Equal(60, (await JsonOf(limited)).GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(otherClient, "/api/client/login", "x")).StatusCode);
    }

    // Replacing and the unique index

    [Fact]
    public async Task Login_SecondLoginOnAPc_ReplacesTheFirst()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, "alice");
        await AddAccountAsync(app, "bob");

        var first = await LoggedInSessionIdAsync(client, pc, "alice");
        var second = await LoggedInSessionIdAsync(client, pc, "bob");

        var sessions = await app.WithDbAsync(db => db.PcSessions.AsNoTracking().OrderBy(s => s.Id).ToListAsync());
        Assert.Equal(2, sessions.Count);
        Assert.Equal(first, sessions[0].Id);
        Assert.Equal("replaced", sessions[0].EndReason);
        Assert.NotNull(sessions[0].EndedAtUtc);
        Assert.Equal(second, sessions[1].Id);
        Assert.Null(sessions[1].EndedAtUtc);
    }

    [Fact]
    public async Task Database_AllowsOnlyOneActiveSessionPerPc()
    {
        using var app = new TestApp();
        var pc = await AddPcAsync(app);
        var now = app.Time.GetUtcNow();
        PcSession NewSession(DateTimeOffset? endedAt) => new()
        {
            PcId = pc.Id,
            PcName = "PC-01",
            UserAccountId = 1,
            Username = "student",
            StartedAtUtc = now,
            EndedAtUtc = endedAt,
            EndReason = endedAt is null ? null : "logout",
        };

        await app.WithDbAsync(async db =>
        {
            db.PcSessions.AddRange(NewSession(now), NewSession(now), NewSession(null));
            await db.SaveChangesAsync();
            return 0;
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => app.WithDbAsync(async db =>
        {
            db.PcSessions.Add(NewSession(null));
            await db.SaveChangesAsync();
            return 0;
        }));
    }

    [Fact]
    public async Task Login_OneAccountOnTwoPcs_KeepsSeparateTimes()
    {
        using var app = new TestApp();
        using var clientA = app.CreatePcClient("192.168.1.50");
        using var clientB = app.CreatePcClient("192.168.1.51");
        var pcA = await AddPcAsync(app, "PC-01");
        var pcB = await AddPcAsync(app, "PC-02");
        await AddAccountAsync(app);

        await LoggedInSessionIdAsync(clientA, pcA);
        app.Time.Advance(TimeSpan.FromMinutes(10));
        await LoggedInSessionIdAsync(clientB, pcB);
        app.Time.Advance(TimeSpan.FromMinutes(5));

        var a = (await JsonOf(await HeartbeatAsync(clientA, pcA))).GetProperty("session");
        var b = (await JsonOf(await HeartbeatAsync(clientB, pcB))).GetProperty("session");
        Assert.Equal(2700, a.GetProperty("secondsRemaining").GetInt32());
        Assert.Equal(3300, b.GetProperty("secondsRemaining").GetInt32());
        Assert.Equal(2, await app.WithDbAsync(db => db.PcSessions.CountAsync(s => s.EndedAtUtc == null)));
    }

    [Fact]
    public async Task Login_AfterLogout_GivesAFullAllowanceAgain()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(40));
        await LogoutAsync(client, pc);

        var response = await LoginAsync(client, pc);

        Assert.Equal(3600, (await JsonOf(response)).GetProperty("secondsRemaining").GetInt32());
    }

    // Logout

    [Fact]
    public async Task Logout_EndsTheSession_AndIsSafeToRepeat()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);

        Assert.Equal(HttpStatusCode.NoContent, (await LogoutAsync(client, pc)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await LogoutAsync(client, pc)).StatusCode);

        var session = await StoredSessionAsync(app, sessionId);
        Assert.Equal("logout", session.EndReason);
        Assert.NotNull(session.EndedAtUtc);
        Assert.Equal(JsonValueKind.Null, (await JsonOf(await HeartbeatAsync(client, pc))).GetProperty("session").ValueKind);
    }

    [Fact]
    public async Task Logout_WithoutASession_Returns204()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);

        Assert.Equal(HttpStatusCode.NoContent, (await LogoutAsync(client, pc)).StatusCode);
    }

    [Fact]
    public async Task Logout_OnlyEndsThatPcsSession()
    {
        using var app = new TestApp();
        using var clientA = app.CreatePcClient("192.168.1.50");
        using var clientB = app.CreatePcClient("192.168.1.51");
        var pcA = await AddPcAsync(app, "PC-01");
        var pcB = await AddPcAsync(app, "PC-02");
        await AddAccountAsync(app);
        await LoggedInSessionIdAsync(clientA, pcA);
        var sessionB = await LoggedInSessionIdAsync(clientB, pcB);

        await LogoutAsync(clientA, pcA);

        Assert.Null((await StoredSessionAsync(app, sessionB)).EndedAtUtc);
    }

    // Heartbeat

    [Fact]
    public async Task Heartbeat_WithoutSession_ReturnsNullSessionAndNullReason()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);

        var response = await HeartbeatAsync(client, pc);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("session").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("endedReason").ValueKind);
    }

    [Fact]
    public async Task Heartbeat_ReturnsTheSession_AndSecondsDecreaseWithTheClock()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);

        app.Time.Advance(TimeSpan.FromSeconds(100));
        var first = await JsonOf(await HeartbeatAsync(client, pc, sessionId));
        app.Time.Advance(TimeSpan.FromMilliseconds(500));
        var second = await JsonOf(await HeartbeatAsync(client, pc, sessionId));

        var session = first.GetProperty("session");
        Assert.Equal(sessionId, session.GetProperty("sessionId").GetInt32());
        Assert.Equal("student", session.GetProperty("username").GetString());
        Assert.Equal(60, session.GetProperty("time").GetProperty("minutes").GetInt32());
        Assert.Equal(3500, session.GetProperty("secondsRemaining").GetInt32());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("endedReason").ValueKind);
        // 3499.5 seconds left, rounded up.
        Assert.Equal(3500, second.GetProperty("session").GetProperty("secondsRemaining").GetInt32());
    }

    [Fact]
    public async Task Heartbeat_WithOpenTime_HasNullSecondsRemaining()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: null);
        await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromDays(3));

        var session = (await JsonOf(await HeartbeatAsync(client, pc))).GetProperty("session");

        Assert.Equal("open-time", session.GetProperty("time").GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("secondsRemaining").ValueKind);
    }

    [Fact]
    public async Task Heartbeat_ReportsIsAdminUnlockFalse_ForALoginSession()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);

        var session = (await JsonOf(await HeartbeatAsync(client, pc, sessionId))).GetProperty("session");

        Assert.False(session.GetProperty("isAdminUnlock").GetBoolean());
    }


    // Creates an admin unlock on the PC through the admin route and returns the new session's id.
    private static async Task<int> AdminUnlockAsync(TestApp app, HttpClient admin, TestPc pc)
    {
        var response = await admin.PostAsJsonAsync($"/api/pcs/{pc.Id}/unlock", new { });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return await app.WithDbAsync(db => db.PcSessions.AsNoTracking().Where(s => s.IsAdminUnlock && s.EndedAtUtc == null).Select(s => s.Id).SingleAsync());
    }

    [Fact]
    public async Task Heartbeat_AdminUnlock_IsAdoptedByAnOnlinePcThatDoesNotKnowIt()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var unlockId = await AdminUnlockAsync(app, admin, pc);
        app.Time.Advance(TimeSpan.FromSeconds(2));

        var body = await JsonOf(await HeartbeatAsync(client, pc));

        var session = body.GetProperty("session");
        Assert.Equal(unlockId, session.GetProperty("sessionId").GetInt32());
        Assert.True(session.GetProperty("isAdminUnlock").GetBoolean());
        Assert.Null((await StoredSessionAsync(app, unlockId)).EndedAtUtc);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(60)]
    public async Task Heartbeat_AdminUnlock_IsEndedAsConnectionLost_WhenAnOfflinePcDoesNotKnowIt(int secondsOffline)
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var unlockId = await AdminUnlockAsync(app, admin, pc);
        app.Time.Advance(TimeSpan.FromSeconds(secondsOffline));

        var body = await JsonOf(await HeartbeatAsync(client, pc));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("session").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("endedReason").ValueKind);
        var stored = await StoredSessionAsync(app, unlockId);
        Assert.Equal(SessionLifecycle.ConnectionLost, stored.EndReason);
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.EndedAtUtc!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Heartbeat_AdminUnlock_StaysEnded_WhenTheHeartbeatFailsBeforeLastSeenIsWritten()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var unlockId = await AdminUnlockAsync(app, admin, pc);
        app.Time.Advance(TimeSpan.FromSeconds(60));
        // A heartbeat that dies partway must not leave a fresh last-seen time and the unlock still open,
        // or the PC's next heartbeat would look online and adopt the unlock.
        await app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER FailLastSeen BEFORE UPDATE OF LastSeenUtc ON Pcs
            BEGIN
                SELECT RAISE(ABORT, 'simulated failure');
            END
            """));

        try
        {
            await HeartbeatAsync(client, pc);
        }
        catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
        {
            // The test host passes the simulated failure through.
        }

        await app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailLastSeen"));
        Assert.Equal(SessionLifecycle.ConnectionLost, (await StoredSessionAsync(app, unlockId)).EndReason);

        var body = await JsonOf(await HeartbeatAsync(client, pc));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("session").ValueKind);
    }

    [Fact]
    public async Task Heartbeat_AdminUnlock_IsEndedAsConnectionLost_WhenTheLastSeenTimeIsInTheFuture()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var unlockId = await AdminUnlockAsync(app, admin, pc);
        // A last-seen time in the future (as after the server clock moved back) counts as offline.
        var future = app.Time.GetUtcNow().AddSeconds(5);
        await app.WithDbAsync(db => db.Pcs.Where(p => p.Id == pc.Id).ExecuteUpdateAsync(setters => setters.SetProperty(p => p.LastSeenUtc, future)));

        var body = await JsonOf(await HeartbeatAsync(client, pc));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("session").ValueKind);
        Assert.Equal(SessionLifecycle.ConnectionLost, (await StoredSessionAsync(app, unlockId)).EndReason);
    }

    [Fact]
    public async Task Heartbeat_AdminUnlock_IsKeptWhenAnOfflinePcReportsItsId_AfterANetworkBlip()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var unlockId = await AdminUnlockAsync(app, admin, pc);
        app.Time.Advance(TimeSpan.FromSeconds(2));
        await HeartbeatAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        var body = await JsonOf(await HeartbeatAsync(client, pc, unlockId));

        Assert.Equal(unlockId, body.GetProperty("session").GetProperty("sessionId").GetInt32());
        Assert.Null((await StoredSessionAsync(app, unlockId)).EndedAtUtc);
    }

    [Fact]
    public async Task Heartbeat_OfflinePcWithALoginSession_KeepsIt()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(60));

        var body = await JsonOf(await HeartbeatAsync(client, pc));

        Assert.Equal(sessionId, body.GetProperty("session").GetProperty("sessionId").GetInt32());
        Assert.Null((await StoredSessionAsync(app, sessionId)).EndedAtUtc);
    }

    [Fact]
    public async Task Heartbeat_AtExpiry_EndsTheSessionWithTimeExpired()

    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 1);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, (await JsonOf(await HeartbeatAsync(client, pc, sessionId))).GetProperty("session").GetProperty("secondsRemaining").GetInt32());
        app.Time.Advance(TimeSpan.FromSeconds(1));

        var body = await JsonOf(await HeartbeatAsync(client, pc, sessionId));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("session").ValueKind);
        Assert.Equal("time-expired", body.GetProperty("endedReason").GetString());
        var stored = await StoredSessionAsync(app, sessionId);
        Assert.Equal("time-expired", stored.EndReason);
        Assert.Equal(stored.ExpiresAtUtc, stored.EndedAtUtc);
    }

    [Fact]
    public async Task Heartbeat_AfterExpiry_AllowsANewLoginWithAFullAllowance()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: 1);
        var first = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(5));

        // The expired session was never closed by a heartbeat; login still works and records why it ended.
        var response = await LoginAsync(client, pc);

        Assert.Equal(60, (await JsonOf(response)).GetProperty("secondsRemaining").GetInt32());
        Assert.Equal("time-expired", (await StoredSessionAsync(app, first)).EndReason);
    }

    [Fact]
    public async Task Heartbeat_AfterLogout_ReportsLogoutForTheSentId()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        await LogoutAsync(client, pc);

        var body = await JsonOf(await HeartbeatAsync(client, pc, sessionId));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("session").ValueKind);
        Assert.Equal("logout", body.GetProperty("endedReason").GetString());
    }

    [Fact]
    public async Task Heartbeat_AfterBeingReplaced_ReportsReplacedForTheOldId_AndTheNewSession()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var first = await LoggedInSessionIdAsync(client, pc);
        var second = await LoggedInSessionIdAsync(client, pc);

        var body = await JsonOf(await HeartbeatAsync(client, pc, first));

        Assert.Equal(second, body.GetProperty("session").GetProperty("sessionId").GetInt32());
        Assert.Equal("replaced", body.GetProperty("endedReason").GetString());
    }

    [Fact]
    public async Task Heartbeat_SendingTheActiveId_ReportsNoEndedReason()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);

        var body = await JsonOf(await HeartbeatAsync(client, pc, sessionId));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("endedReason").ValueKind);
    }

    [Fact]
    public async Task Heartbeat_AfterRemovedPcSession_ReportsPcRemovedInTheDatabase()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(20));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/pcs/{pc.Id}")).StatusCode);

        var session = await StoredSessionAsync(app, sessionId);
        Assert.Equal("pc-removed", session.EndReason);
        Assert.NotNull(session.EndedAtUtc);
        Assert.Equal(HttpStatusCode.Unauthorized, (await HeartbeatAsync(client, pc)).StatusCode);
    }

    [Fact]
    public async Task Heartbeat_NeverRevealsAnotherPcsSession()
    {
        using var app = new TestApp();
        using var clientA = app.CreatePcClient("192.168.1.50");
        using var clientB = app.CreatePcClient("192.168.1.51");
        var pcA = await AddPcAsync(app, "PC-01");
        var pcB = await AddPcAsync(app, "PC-02");
        await AddAccountAsync(app);
        var ended = await LoggedInSessionIdAsync(clientA, pcA);
        await LogoutAsync(clientA, pcA);
        var active = await LoggedInSessionIdAsync(clientA, pcA);

        var endedBody = await JsonOf(await HeartbeatAsync(clientB, pcB, ended));
        var activeBody = await JsonOf(await HeartbeatAsync(clientB, pcB, active));

        foreach (var body in new[] { endedBody, activeBody })
        {
            Assert.Equal(JsonValueKind.Null, body.GetProperty("session").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("endedReason").ValueKind);
        }
    }

    [Theory]
    [InlineData("{not json", "application/json")]
    [InlineData("", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("""{"sessionId":"abc"}""", "application/json")]
    [InlineData("""{"sessionId":99999999999}""", "application/json")]
    [InlineData("""{"sessionId":1}""", "text/plain")]
    public async Task Heartbeat_WithBadBody_StillCountsAsAHeartbeatWithoutASessionId(string body, string contentType)
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(5));

        var response = await PostAsync(client, "/api/client/heartbeat", pc.Token, new StringContent(body, Encoding.UTF8, contentType));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonOf(response);
        Assert.Equal(sessionId, json.GetProperty("session").GetProperty("sessionId").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("endedReason").ValueKind);
        var stored = await app.WithDbAsync(db => db.Pcs.AsNoTracking().SingleAsync());
        Assert.Equal(app.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.LastSeenUtc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Heartbeat_WithNoBody_Works()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/api/client/heartbeat", pc.Token)).StatusCode);
    }

    // Admin side effects

    [Fact]
    public async Task DisablingAnAccount_EndsItsSessions_AndReenablingDoesNotReviveThem()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var clientA = app.CreatePcClient("192.168.1.50");
        using var clientB = app.CreatePcClient("192.168.1.51");
        var pcA = await AddPcAsync(app, "PC-01");
        var pcB = await AddPcAsync(app, "PC-02");
        var accountId = await AddAccountAsync(app);
        await AddAccountAsync(app, "other");
        var sessionA = await LoggedInSessionIdAsync(clientA, pcA);
        var sessionB = await LoggedInSessionIdAsync(clientB, pcB, "other");

        var disabled = await admin.PatchAsJsonAsync($"/api/accounts/{accountId}/status", new { isActive = false });

        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var body = await JsonOf(await HeartbeatAsync(clientA, pcA, sessionA));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("session").ValueKind);
        Assert.Equal("account-disabled", body.GetProperty("endedReason").GetString());
        Assert.Null((await StoredSessionAsync(app, sessionB)).EndedAtUtc);

        await admin.PatchAsJsonAsync($"/api/accounts/{accountId}/status", new { isActive = true });
        var afterEnable = await JsonOf(await HeartbeatAsync(clientA, pcA, sessionA));
        Assert.Equal(JsonValueKind.Null, afterEnable.GetProperty("session").ValueKind);
        Assert.Equal("account-disabled", (await StoredSessionAsync(app, sessionA)).EndReason);    }

    [Fact]
    public async Task DeletingAnAccount_EndsItsSessions()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var accountId = await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/accounts/{accountId}")).StatusCode);

        var body = await JsonOf(await HeartbeatAsync(client, pc, sessionId));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("session").ValueKind);
        Assert.Equal("account-deleted", body.GetProperty("endedReason").GetString());
    }

    [Fact]
    public async Task ChangingDefaultTimeOrPassword_DoesNotAffectARunningSession()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        var accountId = await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromMinutes(10));

        var update = await admin.PutAsJsonAsync(
            $"/api/accounts/{accountId}",
            new { username = "student", password = "a brand new pass", defaultTime = new { mode = "time-limit", minutes = 5 } });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var session = (await JsonOf(await HeartbeatAsync(client, pc, sessionId))).GetProperty("session");
        Assert.Equal(3000, session.GetProperty("secondsRemaining").GetInt32());
        Assert.Equal(60, session.GetProperty("time").GetProperty("minutes").GetInt32());
    }

    [Fact]
    public async Task PcList_ShowsTheActiveSession()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        await AddPcAsync(app, "PC-02");
        await LoggedInSessionIdAsync(client, pc);
        app.Time.Advance(TimeSpan.FromSeconds(5));
        var startedAt = app.Time.GetUtcNow().AddSeconds(-5);

        var entries = (await JsonOf(await admin.GetAsync("/api/pcs"))).EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!);

        var session = entries["PC-01"].GetProperty("session");
        Assert.Equal(
            new[] { "secondsRemaining", "startedAt", "time", "username" },
            session.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("student", session.GetProperty("username").GetString());
        Assert.Equal(startedAt.ToUnixTimeMilliseconds(), session.GetProperty("startedAt").GetDateTimeOffset().ToUnixTimeMilliseconds());
        Assert.Equal(60, session.GetProperty("time").GetProperty("minutes").GetInt32());
        Assert.Equal(3595, session.GetProperty("secondsRemaining").GetInt32());
        Assert.Equal(JsonValueKind.Null, entries["PC-02"].GetProperty("session").ValueKind);
    }

    [Fact]
    public async Task PcList_SessionIsNullWhenOfflineOrExpiredByTheClock()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var clientA = app.CreatePcClient("192.168.1.50");
        using var clientB = app.CreatePcClient("192.168.1.51");
        var offline = await AddPcAsync(app, "PC-01");
        var expired = await AddPcAsync(app, "PC-02");
        await AddAccountAsync(app, minutes: 1);
        await LoggedInSessionIdAsync(clientA, offline);
        await LoggedInSessionIdAsync(clientB, expired);

        app.Time.Advance(TimeSpan.FromSeconds(50));
        await HeartbeatAsync(clientB, expired);
        app.Time.Advance(TimeSpan.FromSeconds(12));

        var entries = (await JsonOf(await admin.GetAsync("/api/pcs"))).EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!);

        Assert.False(entries["PC-01"].GetProperty("isOnline").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entries["PC-01"].GetProperty("session").ValueKind);
        Assert.True(entries["PC-02"].GetProperty("isOnline").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entries["PC-02"].GetProperty("session").ValueKind);
        Assert.Equal(2, await app.WithDbAsync(db => db.PcSessions.CountAsync(s => s.EndedAtUtc == null)));
    }

    [Fact]
    public async Task PcList_OpenTimeSession_HasNoMinutesAndNullSeconds()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app, minutes: null);
        await LoggedInSessionIdAsync(client, pc);

        var session = (await JsonOf(await admin.GetAsync("/api/pcs")))[0].GetProperty("session");

        Assert.Single(session.GetProperty("time").EnumerateObject());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("secondsRemaining").ValueKind);
    }

    // Channels and the clock

    [Fact]
    public async Task AdminPort_CannotReachLoginOrLogout()
    {
        using var app = new TestApp();
        using var browser = app.CreateBrowser();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);

        var login = await PostAsync(browser, "/api/client/login", pc.Token, JsonContent.Create(new { username = "student", password = StudentPassword }));
        var logout = await PostAsync(browser, "/api/client/logout", pc.Token);

        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, logout.StatusCode);
        Assert.Equal(0, await app.WithDbAsync(db => db.PcSessions.CountAsync()));
    }

    [Fact]
    public async Task PcPort_CannotReachAdminAccountEndpoints()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync("/api/accounts/1/status", new { isActive = false })).StatusCode);
    }

    [Fact]
    public async Task SessionTime_ComesFromTheServerClock()
    {
        using var app = new TestApp();
        using var client = app.CreatePcClient();
        var pc = await AddPcAsync(app);
        await AddAccountAsync(app);
        var sessionId = await LoggedInSessionIdAsync(client, pc);

        // The PC sends nothing about time; only the server's clock moves the countdown.
        app.Time.Advance(TimeSpan.FromMinutes(30));
        var body = await JsonOf(await HeartbeatAsync(client, pc, sessionId));

        Assert.Equal(1800, body.GetProperty("session").GetProperty("secondsRemaining").GetInt32());
    }
}
