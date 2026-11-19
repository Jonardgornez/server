using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SetCookieHeaderValue = Microsoft.Net.Http.Headers.SetCookieHeaderValue;

namespace LabGuard.Server.Tests;

public class AuthEndpointTests
{
    private const string WrongCredentialsMessage = "Wrong username or password.";

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { username, password });

    private static async Task<HttpClient> SignedInAsync(TestApp app)
    {
        var client = app.CreateBrowser();
        var response = await LoginAsync(client, TestApp.Username, TestApp.Password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static async Task FailLoginsAsync(HttpClient client, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var response = await LoginAsync(client, TestApp.Username, "wrong password");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private static Task<HttpResponseMessage> ChangePasswordAsync(HttpClient client, string current, string next) =>
        client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = current, newPassword = next });

    private static Task<HttpResponseMessage> SetIdleTimeoutAsync(HttpClient admin, double minutes) =>
        admin.PutAsJsonAsync("/api/settings", new
        {
            defaultApproval = new { mode = "time-limit", minutes = 60 },
            notice = new { isEnabled = true, text = "Notice" },
            idleTimeoutMinutes = minutes,
            defaultViewMode = "notified",
            powerCountdownSeconds = 30,
            lockMessage = "This computer has been locked by the administrator.",
        });

    // Saves the idle limit, then signs in a new session that carries it.
    private static async Task<HttpClient> SignedInWithIdleLimitAsync(TestApp app, double minutes)
    {
        using (var admin = await SignedInAsync(app))
        {
            Assert.Equal(HttpStatusCode.OK, (await SetIdleTimeoutAsync(admin, minutes)).StatusCode);
        }

        return await SignedInAsync(app);
    }

    private static string? AdminSetCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.SingleOrDefault(value => value.StartsWith(AuthServiceExtensions.CookieName + "=", StringComparison.Ordinal))
            : null;

    // The Expires attribute has whole seconds, so it is compared to the second.
    private static void AssertExpiresAbout(DateTimeOffset expected, string setCookie)
    {
        var expires = SetCookieHeaderValue.Parse(setCookie).Expires;
        Assert.NotNull(expires);
        Assert.InRange(expires.Value, expected.AddSeconds(-1), expected.AddSeconds(1));
    }

    private static async Task<HttpResponseMessage> BackgroundRefreshAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add(AuthServiceExtensions.BackgroundRefreshHeader, "1");
        return await client.SendAsync(request);
    }

    // Seeding

    [Fact]
    public async Task Startup_InDevelopment_SeedsAdminOnceWithHashedPassword()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();
        await client.GetAsync("/api/health");

        var accounts = await app.WithDbAsync(db => db.AdminAccounts.ToListAsync());

        var account = Assert.Single(accounts);
        Assert.Equal(TestApp.Username, account.Username);
        Assert.NotEqual(TestApp.Password, account.PasswordHash);
        Assert.DoesNotContain(TestApp.Password, account.PasswordHash);

        // A second host on the same database must not seed again.
        await app.WithDbAsync(async db =>
        {
            var services = app.Services;
            await AdminSeeder.SeedAsync(
                db,
                services.GetRequiredService<IConfiguration>(),
                services.GetRequiredService<IHostEnvironment>(),
                services.GetRequiredService<IPasswordHasher<AdminAccount>>(),
                NullLogger.Instance);
            return 0;
        });
        Assert.Single(await app.WithDbAsync(db => db.AdminAccounts.ToListAsync()));
    }

    [Fact]
    public async Task Startup_OutsideDevelopment_DoesNotSeed()
    {
        using var app = new TestApp("Production");
        using var client = app.CreateClient();
        await client.GetAsync("/api/health");

        var count = await app.WithDbAsync(db => db.AdminAccounts.CountAsync());

        Assert.Equal(0, count);
    }

    // Login

    [Fact]
    public async Task Login_WithCorrectCredentials_ReturnsUsernameAndSecureCookieAttributes()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();

        var response = await LoginAsync(client, TestApp.Username, TestApp.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TestApp.Username, (await JsonOf(response)).GetProperty("username").GetString());
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("lykantra.admin="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_UsernameIsCaseInsensitive()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();

        var response = await LoginAsync(client, TestApp.Username.ToUpperInvariant(), TestApp.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_WrongPasswordAndUnknownUsername_ReturnIdentical401()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();

        var wrongPassword = await LoginAsync(client, TestApp.Username, "nope nope");
        var unknownUser = await LoginAsync(client, "nobody", "nope nope");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        var wrongPasswordBody = await wrongPassword.Content.ReadAsStringAsync();
        Assert.Equal(wrongPasswordBody, await unknownUser.Content.ReadAsStringAsync());
        Assert.Contains(WrongCredentialsMessage, wrongPasswordBody);
        Assert.False(wrongPassword.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("", "password1")]
    [InlineData("   ", "password1")]
    [InlineData("tester", "")]
    [InlineData("tester", "   ")]
    public async Task Login_WithBlankField_Returns400(string username, string password)
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();

        var response = await LoginAsync(client, username, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await JsonOf(response)).GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Login_WithMissingFields_Returns400()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(65, 10)]
    [InlineData(5, 129)]
    public async Task Login_WithOversizedField_Returns400(int usernameLength, int passwordLength)
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();

        var response = await LoginAsync(client, new string('a', usernameLength), new string('b', passwordLength));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await JsonOf(response)).GetProperty("error").GetString()));
    }

    // Lockout

    [Fact]
    public async Task Login_AfterFiveFailures_Returns429WithRetryAfter()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        await FailLoginsAsync(client, 5);

        var response = await LoginAsync(client, TestApp.Username, "wrong password");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var retryAfter = Assert.Single(response.Headers.GetValues("Retry-After"));
        Assert.Equal("30", retryAfter);
        var body = await JsonOf(response);
        Assert.Equal(30, body.GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal("Too many attempts. Try again in 30 seconds.", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Login_UnknownUsernameFailuresCountTowardLockout()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        for (var i = 0; i < 5; i++)
        {
            await LoginAsync(client, "nobody", "wrong password");
        }

        var response = await LoginAsync(client, TestApp.Username, TestApp.Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task Login_CorrectPasswordDuringLock_IsRejectedUntilLockExpires()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        await FailLoginsAsync(client, 5);

        var duringLock = await LoginAsync(client, TestApp.Username, TestApp.Password);
        app.Time.Advance(TimeSpan.FromSeconds(10));
        var stillLocked = await LoginAsync(client, TestApp.Username, TestApp.Password);
        app.Time.Advance(TimeSpan.FromSeconds(20));
        var afterLock = await LoginAsync(client, TestApp.Username, TestApp.Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, duringLock.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, stillLocked.StatusCode);
        Assert.Equal("20", Assert.Single(stillLocked.Headers.GetValues("Retry-After")));
        Assert.Equal(HttpStatusCode.OK, afterLock.StatusCode);
    }

    [Fact]
    public async Task Login_Success_ResetsFailureCounter()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        await FailLoginsAsync(client, 4);
        var success = await LoginAsync(client, TestApp.Username, TestApp.Password);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);

        await FailLoginsAsync(client, 4);
        var next = await LoginAsync(client, TestApp.Username, TestApp.Password);

        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Lockout_IsPersistedOnTheAccountRow()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        await FailLoginsAsync(client, 5);

        var account = await app.WithDbAsync(db => db.AdminAccounts.SingleAsync());

        Assert.NotNull(account.LockedUntilUtc);
        Assert.Equal(app.Time.GetUtcNow().AddSeconds(30).ToUnixTimeMilliseconds(), account.LockedUntilUtc!.Value.ToUnixTimeMilliseconds());
    }

    // Session endpoints

    [Fact]
    public async Task Me_WithoutCookie_Returns401JsonNotRedirect()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.False(string.IsNullOrEmpty((await JsonOf(response)).GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Me_WithCookie_ReturnsUsername()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TestApp.Username, (await JsonOf(response)).GetProperty("username").GetString());
    }

    [Fact]
    public async Task UnknownProtectedRoute_WithoutCookie_Returns401NotRedirect()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();

        var response = await client.GetAsync("/api/anything");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Logout_EndsAccess()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new { });
        var me = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Logout_Anonymous_SucceedsWithoutEndingAnySession()
    {
        using var app = new TestApp();
        using var anonymous = app.CreateBrowser();
        using var client = await SignedInAsync(app);
        var versionBefore = await app.WithDbAsync(db => db.AdminAccounts.Select(a => a.SessionVersion).SingleAsync());

        var anonymousLogout = await anonymous.PostAsJsonAsync("/api/auth/logout", new { });

        Assert.Equal(HttpStatusCode.NoContent, anonymousLogout.StatusCode);
        Assert.Equal(versionBefore, await app.WithDbAsync(db => db.AdminAccounts.Select(a => a.SessionVersion).SingleAsync()));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesTheSessionOnTheServer_SoAReplayedCookieGets401()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        var login = await LoginAsync(client, TestApp.Username, TestApp.Password);
        var cookie = AdminSetCookie(login)!.Split(';')[0];

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/logout", new { })).StatusCode);

        using var replay = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Logout_SignsOutTheAdminsOtherBrowsersToo()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        using var otherBrowser = await SignedInAsync(app);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/logout", new { })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await otherBrowser.GetAsync("/api/auth/me")).StatusCode);
        using var fresh = await SignedInAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_WithASessionThatIsAlreadyInvalid_SucceedsAndClearsTheCookie()
    {
        using var app = new TestApp();
        using var stale = await SignedInAsync(app);
        using var admin = await SignedInAsync(app);
        Assert.Equal(HttpStatusCode.NoContent, (await ChangePasswordAsync(admin, TestApp.Password, "a brand new password")).StatusCode);
        using var current = app.CreateBrowser();
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(current, TestApp.Username, "a brand new password")).StatusCode);

        var logout = await stale.PostAsJsonAsync("/api/auth/logout", new { });

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var cleared = AdminSetCookie(logout);
        Assert.NotNull(cleared);
        Assert.True(SetCookieHeaderValue.Parse(cleared).Expires < app.Time.GetUtcNow());
        Assert.Equal(HttpStatusCode.OK, (await current.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("application/x-www-form-urlencoded", "a=1")]
    [InlineData("text/plain", "{}")]
    [InlineData(null, "")]
    public async Task Logout_WithoutAJsonContentType_Returns400AndRevokesNothing(string? contentType, string body)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        using var otherBrowser = await SignedInAsync(app);
        using var content = new StringContent(body);
        content.Headers.ContentType = contentType is null ? null : new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        var logout = await client.PostAsync("/api/auth/logout", content);

        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
        Assert.Null(AdminSetCookie(logout));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherBrowser.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutABody_Returns400AndRevokesNothing()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var logout = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    // Changing the idle timeout

    [Fact]
    public async Task IdleTimeout_FromNeverToMinutes_SignsOutTheOtherBrowsers()
    {
        using var app = new TestApp();
        using var saver = await SignedInAsync(app);
        using var otherBrowser = await SignedInAsync(app);
        using var copied = app.CreateBrowser();
        var oldCookie = AdminSetCookie(await LoginAsync(copied, TestApp.Username, TestApp.Password))!.Split(';')[0];

        Assert.Equal(HttpStatusCode.OK, (await SetIdleTimeoutAsync(saver, 5)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await otherBrowser.GetAsync("/api/auth/me")).StatusCode);
        using var replay = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", oldCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task IdleTimeout_FromNeverToMinutes_KeepsTheSavingBrowserSignedInUnderTheNewRule()
    {
        using var app = new TestApp();
        using var saver = await SignedInAsync(app);

        var saved = await SetIdleTimeoutAsync(saver, 5);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var cookie = AdminSetCookie(saved);
        Assert.NotNull(cookie);
        Assert.Null(SetCookieHeaderValue.Parse(cookie).Expires);
        app.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(HttpStatusCode.OK, (await saver.GetAsync("/api/auth/me")).StatusCode);
        app.Time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Unauthorized, (await saver.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData(30.0, 0.0)]
    [InlineData(30.0, 5.0)]
    [InlineData(0.0, 0.0)]
    public async Task IdleTimeout_OtherChanges_SignNoOneOutAndApplyFromTheNextSignIn(double from, double to)
    {
        using var app = new TestApp();
        using var saver = await SignedInWithIdleLimitAsync(app, from);
        using var otherBrowser = await SignedInAsync(app);
        var versionBefore = await app.WithDbAsync(db => db.AdminAccounts.Select(a => a.SessionVersion).SingleAsync());

        var saved = await SetIdleTimeoutAsync(saver, to);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(versionBefore, await app.WithDbAsync(db => db.AdminAccounts.Select(a => a.SessionVersion).SingleAsync()));
        app.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(HttpStatusCode.OK, (await otherBrowser.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_ExpiresAfter30IdleMinutes_AndSlidesWhenActive()
    {
        using var app = new TestApp();
        using var client = await SignedInWithIdleLimitAsync(app, 30);

        app.Time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        app.Time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        app.Time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public void CookieOptions_AreConfiguredAsRequired()
    {
        using var app = new TestApp();
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal("lykantra.admin", options.Cookie.Name);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Strict, options.Cookie.SameSite);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, options.Cookie.SecurePolicy);
        Assert.Equal(TimeSpan.FromMinutes(ServerSettings.MaxIdleTimeoutMinutes), options.ExpireTimeSpan);
        Assert.False(options.SlidingExpiration);
    }

    // Change password

    [Fact]
    public async Task ChangePassword_WithoutSession_Returns401()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();

        var response = await ChangePasswordAsync(client, TestApp.Password, "new password 1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WithWrongCurrentPassword_Returns400()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await ChangePasswordAsync(client, "not my password", "new password 1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await JsonOf(response)).GetProperty("error").GetString()));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("1234567")]
    public async Task ChangePassword_WithTooShortNewPassword_Returns400(string newPassword)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await ChangePasswordAsync(client, TestApp.Password, newPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WithTooLongNewPassword_Returns400()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await ChangePasswordAsync(client, TestApp.Password, new string('x', 129));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WithSameAsCurrent_Returns400()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await ChangePasswordAsync(client, TestApp.Password, TestApp.Password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_Success_EndsAllSessionsAndSwitchesPassword()
    {
        const string newPassword = "a brand new password";
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        using var otherSession = await SignedInAsync(app);

        var response = await ChangePasswordAsync(client, TestApp.Password, newPassword);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherSession.GetAsync("/api/auth/me")).StatusCode);

        using var fresh = app.CreateBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(fresh, TestApp.Username, TestApp.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(fresh, TestApp.Username, newPassword)).StatusCode);
    }

    [Fact]
    public async Task OldCookie_IsRejectedAfterPasswordChange_EvenIfReplayed()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        var login = await LoginAsync(client, TestApp.Username, TestApp.Password);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
        await ChangePasswordAsync(client, TestApp.Password, "a brand new password");

        using var replay = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", cookie);
        var response = await replay.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("        ")]
    [InlineData("\t\t\t\t\t\t\t\t")]
    public async Task ChangePassword_WithWhitespaceOnlyNewPassword_Returns400AndKeepsOldPassword(string newPassword)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var response = await ChangePasswordAsync(client, TestApp.Password, newPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        using var fresh = app.CreateBrowser();
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(fresh, TestApp.Username, TestApp.Password)).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_AfterFiveWrongCurrentPasswords_Returns429WithRetryAfter()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        for (var i = 0; i < 5; i++)
        {
            var wrong = await ChangePasswordAsync(client, "wrong password", "new password 1");
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        var locked = await ChangePasswordAsync(client, "wrong password", "new password 1");
        var correctDuringLock = await ChangePasswordAsync(client, TestApp.Password, "new password 1");

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("30", Assert.Single(locked.Headers.GetValues("Retry-After")));
        Assert.Equal(30, (await JsonOf(locked)).GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal(HttpStatusCode.TooManyRequests, correctDuringLock.StatusCode);

        app.Time.Advance(TimeSpan.FromSeconds(31));
        var afterLock = await ChangePasswordAsync(client, TestApp.Password, "new password 1");
        Assert.Equal(HttpStatusCode.NoContent, afterLock.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_CorrectCurrentPassword_ResetsFailureCounter()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        for (var i = 0; i < 4; i++)
        {
            await ChangePasswordAsync(client, "wrong password", "new password 1");
        }

        // Correct current password but unchanged new password: rejected, yet the counter resets.
        var same = await ChangePasswordAsync(client, TestApp.Password, TestApp.Password);
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
        for (var i = 0; i < 4; i++)
        {
            await ChangePasswordAsync(client, "wrong password", "new password 1");
        }

        var next = await ChangePasswordAsync(client, TestApp.Password, "new password 1");

        Assert.Equal(HttpStatusCode.NoContent, next.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_ValidationFailures_DoNotCountTowardLockout()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        for (var i = 0; i < 6; i++)
        {
            var response = await ChangePasswordAsync(client, "wrong password", "short");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var account = await app.WithDbAsync(db => db.AdminAccounts.SingleAsync());
        Assert.Equal(0, account.FailedLoginCount);
        Assert.Null(account.LockedUntilUtc);
        Assert.Equal(HttpStatusCode.NoContent, (await ChangePasswordAsync(client, TestApp.Password, "new password 1")).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_ValidationError_IsIdenticalForRightAndWrongCurrentPassword()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        var withRight = await ChangePasswordAsync(client, TestApp.Password, "short");
        var withWrong = await ChangePasswordAsync(client, "wrong password", "short");

        Assert.Equal(await withRight.Content.ReadAsStringAsync(), await withWrong.Content.ReadAsStringAsync());
    }

    // Idle timeout

    [Fact]
    public async Task Session_ActiveAtMinute14_StillValidAtMinute40()
    {
        using var app = new TestApp();
        using var client = await SignedInWithIdleLimitAsync(app, 30);

        app.Time.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        app.Time.Advance(TimeSpan.FromMinutes(26));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_ActiveAtMinute14_ExpiresAfter31IdleMinutes()
    {
        using var app = new TestApp();
        using var client = await SignedInWithIdleLimitAsync(app, 30);

        app.Time.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        app.Time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_UsesTheIdleLimitSavedAtItsSignIn()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await SetIdleTimeoutAsync(admin, 5)).StatusCode);
        using var session = await SignedInAsync(app);

        app.Time.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_SignedInBeforeTheChange_KeepsItsOwnLimit()
    {
        using var app = new TestApp();
        using var admin = await SignedInWithIdleLimitAsync(app, 30);

        Assert.Equal(HttpStatusCode.OK, (await SetIdleTimeoutAsync(admin, 5)).StatusCode);
        app.Time.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_WithAFiveMinuteLimit_IsKeptAliveByActivity_AndEndsAfterItsLimit()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await SetIdleTimeoutAsync(admin, 5);
        using var session = await SignedInAsync(app);

        app.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/auth/me")).StatusCode);
        app.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/auth/me")).StatusCode);
        app.Time.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_BackgroundRefresh_DoesNotRenewTheIdleLimit()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await SetIdleTimeoutAsync(admin, 5);
        using var session = await SignedInAsync(app);

        app.Time.Advance(TimeSpan.FromMinutes(4));
        using var background = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        background.Headers.Add(AuthServiceExtensions.BackgroundRefreshHeader, "1");
        Assert.Equal(HttpStatusCode.OK, (await session.SendAsync(background)).StatusCode);
        app.Time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_WithA480MinuteLimit_StaysValidWellPast30Minutes()
    {
        using var app = new TestApp();
        using var admin = await SignedInAsync(app);
        await SetIdleTimeoutAsync(admin, 480);
        using var session = await SignedInAsync(app);

        app.Time.Advance(TimeSpan.FromMinutes(400));
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/auth/me")).StatusCode);
        app.Time.Advance(TimeSpan.FromMinutes(479));
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/auth/me")).StatusCode);
        app.Time.Advance(TimeSpan.FromMinutes(481));

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Session_WithAMinuteLimit_HasABrowserSessionCookie()
    {
        using var app = new TestApp();
        using (var admin = await SignedInAsync(app))
        {
            await SetIdleTimeoutAsync(admin, 30);
        }

        using var client = app.CreateBrowser();

        var login = await LoginAsync(client, TestApp.Username, TestApp.Password);

        var cookie = AdminSetCookie(login);
        Assert.NotNull(cookie);
        Assert.Null(SetCookieHeaderValue.Parse(cookie).Expires);
    }

    [Fact]
    public async Task Session_WithAMinuteLimit_IsRenewedOnEveryRealRequestButNotOnABackgroundRefresh()
    {
        using var app = new TestApp();
        using var client = await SignedInWithIdleLimitAsync(app, 30);

        app.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.NotNull(AdminSetCookie(await client.GetAsync("/api/auth/me")));
        Assert.Null(AdminSetCookie(await BackgroundRefreshAsync(client)));
    }

    // Never idles out (idle timeout 0)

    [Fact]
    public async Task NeverSession_IsTheDefault_AndHasAPersistentCookieThatExpiresIn400Days()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        var signedInAt = app.Time.GetUtcNow();

        var login = await LoginAsync(client, TestApp.Username, TestApp.Password);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(TimeSpan.FromDays(400), AuthServiceExtensions.NeverIdleCookieLifetime);
        var cookie = AdminSetCookie(login);
        Assert.NotNull(cookie);
        AssertExpiresAbout(signedInAt + AuthServiceExtensions.NeverIdleCookieLifetime, cookie);
    }

    [Fact]
    public async Task NeverSession_SavedAfterAMinuteLimit_AppliesFromTheNextSignIn()
    {
        using var app = new TestApp();
        using var admin = await SignedInWithIdleLimitAsync(app, 5);
        Assert.Equal(HttpStatusCode.OK, (await SetIdleTimeoutAsync(admin, 0)).StatusCode);
        using var session = await SignedInAsync(app);

        app.Time.Advance(TimeSpan.FromDays(30));

        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task NeverSession_DoesNotIdleOutAfter30Days()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        app.Time.Advance(TimeSpan.FromDays(30));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task NeverSession_StaysSignedInPast400DaysWhileItIsRenewed()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        app.Time.Advance(TimeSpan.FromDays(399));
        var renewed = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var cookie = AdminSetCookie(renewed);
        Assert.NotNull(cookie);
        AssertExpiresAbout(app.Time.GetUtcNow() + AuthServiceExtensions.NeverIdleCookieLifetime, cookie);

        app.Time.Advance(TimeSpan.FromDays(399));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task NeverSession_EndsWhenItsCookieExpiresWithoutARenewal()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        app.Time.Advance(TimeSpan.FromDays(400) + TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task NeverSession_IsRenewedAtMostOnceADay()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        app.Time.Advance(TimeSpan.FromHours(23));
        var early = await client.GetAsync("/api/auth/me");
        app.Time.Advance(TimeSpan.FromHours(2));
        var renewedAt = app.Time.GetUtcNow();
        var dayLater = await client.GetAsync("/api/auth/me");
        app.Time.Advance(TimeSpan.FromHours(1));
        var soonAfter = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, early.StatusCode);
        Assert.Null(AdminSetCookie(early));
        var renewal = AdminSetCookie(dayLater);
        Assert.NotNull(renewal);
        AssertExpiresAbout(renewedAt + AuthServiceExtensions.NeverIdleCookieLifetime, renewal);
        Assert.Equal(HttpStatusCode.OK, soonAfter.StatusCode);
        Assert.Null(AdminSetCookie(soonAfter));
    }

    [Fact]
    public async Task NeverSession_IsNotRenewedByABackgroundRefresh()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        app.Time.Advance(TimeSpan.FromDays(2));
        var background = await BackgroundRefreshAsync(client);
        var real = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, background.StatusCode);
        Assert.Null(AdminSetCookie(background));
        Assert.NotNull(AdminSetCookie(real));
    }

    [Fact]
    public async Task NeverSession_KeptOnlyByBackgroundRefreshes_EndsWhenItsCookieExpires()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);

        for (var day = 50; day < 400; day += 50)
        {
            app.Time.Advance(TimeSpan.FromDays(50));
            Assert.Equal(HttpStatusCode.OK, (await BackgroundRefreshAsync(client)).StatusCode);
        }

        app.Time.Advance(TimeSpan.FromDays(49));
        Assert.Equal(HttpStatusCode.OK, (await BackgroundRefreshAsync(client)).StatusCode);
        app.Time.Advance(TimeSpan.FromDays(2));

        Assert.Equal(HttpStatusCode.Unauthorized, (await BackgroundRefreshAsync(client)).StatusCode);
    }

    [Fact]
    public async Task NeverSession_EndsOnSignOut()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        app.Time.Advance(TimeSpan.FromDays(30));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/logout", new { })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task NeverSession_EndsOnPasswordChange_EvenIfTheCookieIsReplayed()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        var login = await LoginAsync(client, TestApp.Username, TestApp.Password);
        var cookie = AdminSetCookie(login)!.Split(';')[0];
        using var otherSession = await SignedInAsync(app);
        app.Time.Advance(TimeSpan.FromDays(30));

        var changed = await ChangePasswordAsync(otherSession, TestApp.Password, "a brand new password");

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        using var replay = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task NeverSession_EndsWhenTheAccountIsDeleted()
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        app.Time.Advance(TimeSpan.FromDays(30));

        await app.WithDbAsync(db => db.AdminAccounts.ExecuteDeleteAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    // Request hardening

    [Fact]
    public async Task Request_WithForeignHostHeader_Returns400()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = "evil.example";

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/change-password")]
    public async Task Post_WithNonJsonContentType_DoesNotSucceedAndSetsNoCookie(string path)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        using var anonymous = app.CreateBrowser();
        var body = $"username={TestApp.Username}&password={TestApp.Password}&currentPassword={TestApp.Password}&newPassword=new password 1";
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain");

        var response = await (path.EndsWith("login") ? anonymous : client).PostAsync(path, content);

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType);
        if (path.EndsWith("login"))
        {
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        // The signed-in session is untouched: the password did not change and the session did not end.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        using var fresh = app.CreateBrowser();
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(fresh, TestApp.Username, TestApp.Password)).StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/change-password")]
    public async Task Post_WithMalformedJson_Returns400InvalidRequestWithoutCountingAttempt(string path)
    {
        using var app = new TestApp();
        using var client = await SignedInAsync(app);
        using var anonymous = app.CreateBrowser();

        for (var i = 0; i < 6; i++)
        {
            using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");
            var response = await (path.EndsWith("login") ? anonymous : client).PostAsync(path, content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("Invalid request.", (await JsonOf(response)).GetProperty("error").GetString());
        }

        var account = await app.WithDbAsync(db => db.AdminAccounts.SingleAsync());
        Assert.Equal(0, account.FailedLoginCount);
        Assert.Null(account.LockedUntilUtc);
    }

    // Anonymous endpoints

    [Fact]
    public async Task Health_StaysAnonymous()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenApiDocument_IsAnonymousInDevelopment()
    {
        using var app = new TestApp();
        using var client = app.CreateBrowser();

        var response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
