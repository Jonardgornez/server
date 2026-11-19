using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using LabGuard.Server.Data;
using LabGuard.Server.Screen;
using LabGuard.Server.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Auth;

public record LoginRequest(string? Username, string? Password);
public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public record UserResponse(string Username);
public record ErrorResponse(string Error);
public record RateLimitResponse(string Error, int RetryAfterSeconds);

public static class AuthEndpoints
{
    private const int MaxUsernameLength = 64;
    private const int MaxPasswordLength = 128;
    private const int MinNewPasswordLength = 8;
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromSeconds(30);

    // Serializes login attempts so parallel guesses can't slip past the failure counter.
    private static readonly SemaphoreSlim LoginGate = new(1, 1);

    private static readonly AdminAccount DummyAccount = new();
    private static readonly string DummyHash = new PasswordHasher<AdminAccount>()
        .HashPassword(DummyAccount, Guid.NewGuid().ToString("N"));

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");
        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/logout", (Delegate)LogoutAsync).AllowAnonymous();
        group.MapGet("/me", Me);
        group.MapPost("/change-password", ChangePasswordAsync);
        return app;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http,
        AppDbContext db,
        IPasswordHasher<AdminAccount> hasher,
        TimeProvider clock)
    {
        var request = await ReadBodyAsync<LoginRequest>(http.Request);
        if (request is null)
        {
            return InvalidRequest();
        }

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("Enter a username and password.");
        }

        if (request.Username.Length > MaxUsernameLength || request.Password.Length > MaxPasswordLength)
        {
            return BadRequest("Username or password is too long.");
        }

        await LoginGate.WaitAsync(http.RequestAborted);
        try
        {
            // There is a single admin, so it also carries the lockout for unknown usernames.
            var admin = await db.AdminAccounts.OrderBy(a => a.Id).FirstOrDefaultAsync(http.RequestAborted);
            var now = clock.GetUtcNow();

            if (LockedResponse(admin, now, http) is { } locked)
            {
                return locked;
            }

            var usernameMatches = admin is not null
                && string.Equals(admin.Username, request.Username.Trim(), StringComparison.OrdinalIgnoreCase);

            // Always run one hash verification so timing doesn't reveal whether the username exists.
            var passwordCorrect = VerifyPassword(hasher, usernameMatches ? admin : null, request.Password);

            if (admin is null)
            {
                return WrongCredentials();
            }

            if (!(usernameMatches && passwordCorrect))
            {
                await RecordFailureAsync(db, admin, now, http.RequestAborted);
                return WrongCredentials();
            }

            await ResetFailuresAsync(db, admin, http.RequestAborted);

            // The idle limit is read at sign-in, so a change in settings applies from the next sign-in.
            var settings = await ServerSettingsStore.GetAsync(db, http.RequestAborted);
            await SignInAdminAsync(http, admin, settings.IdleTimeoutMinutes, now);
            return Results.Ok(new UserResponse(admin.Username));
        }
        finally
        {
            LoginGate.Release();
        }
    }

    // Anonymous, so a browser whose session is already invalid can still clear its cookie.
    // A valid session is revoked on the server: every cookie of the account stops working, including copies and other browsers.
    // Only a JSON request is accepted, which a cross-site form can't send without a CORS preflight.
    private static async Task<IResult> LogoutAsync(HttpContext http, ClaimsPrincipal user, AppDbContext db, ScreenStreamHub screens)
    {
        if (!http.Request.HasJsonContentType())
        {
            return InvalidRequest();
        }

        int? signedOutAdminId = user.Identity?.IsAuthenticated == true
            && int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture, out var id)
            && await EndAllSessionsAsync(db, id, http.RequestAborted)
                ? id
                : null;

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (signedOutAdminId is { } adminId)
        {
            await CloseViewsOfAsync(screens, adminId);
        }

        return Results.NoContent();
    }

    // A new session version invalidates every cookie that carries the old one, which ValidateSessionVersionAsync checks on each request.
    // Returns false when the account no longer exists.
    internal static async Task<bool> EndAllSessionsAsync(AppDbContext db, int adminId, CancellationToken cancellationToken) =>
        await db.AdminAccounts
            .Where(a => a.Id == adminId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.SessionVersion, a => a.SessionVersion + 1), cancellationToken) > 0;

    // A live view belongs to a session of the account, so it ends with the account's sessions.
    internal static Task CloseViewsOfAsync(ScreenStreamHub screens, int adminId) =>
        screens.CloseAllOfAdminAsync(adminId, ScreenProtocol.SessionEndedCloseStatus, ScreenProtocol.SessionEndedReason);

    // Issues this browser's cookie with the account's current session version and the given idle rule.
    internal static Task SignInAdminAsync(HttpContext http, AdminAccount admin, int idleTimeoutMinutes, DateTimeOffset now) =>
        http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            BuildPrincipal(admin, idleTimeoutMinutes),
            BuildSessionProperties(idleTimeoutMinutes, now));

    private static IResult Me(ClaimsPrincipal user) =>
        Results.Ok(new UserResponse(user.Identity?.Name ?? ""));

    private static async Task<IResult> ChangePasswordAsync(
        HttpContext http,
        ClaimsPrincipal user,
        AppDbContext db,
        IPasswordHasher<AdminAccount> hasher,
        TimeProvider clock,
        ScreenStreamHub screens)
    {
        var request = await ReadBodyAsync<ChangePasswordRequest>(http.Request);
        if (request is null)
        {
            return InvalidRequest();
        }

        // Checks that don't depend on the current password come first, so they never reveal
        // whether it is right and never count as a failed attempt.
        if (string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
        {
            return BadRequest("Enter your current and new password.");
        }

        if (request.NewPassword.Length < MinNewPasswordLength || request.NewPassword.Length > MaxPasswordLength)
        {
            return BadRequest($"New password must be {MinNewPasswordLength} to {MaxPasswordLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest("New password can't be only spaces.");
        }

        if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture, out var id))
        {
            return SignInRequired();
        }

        await LoginGate.WaitAsync(http.RequestAborted);
        try
        {
            var admin = await db.AdminAccounts.FirstOrDefaultAsync(a => a.Id == id, http.RequestAborted);
            if (admin is null)
            {
                return SignInRequired();
            }

            var now = clock.GetUtcNow();
            if (LockedResponse(admin, now, http) is { } locked)
            {
                return locked;
            }

            if (request.CurrentPassword.Length > MaxPasswordLength || !VerifyPassword(hasher, admin, request.CurrentPassword))
            {
                await RecordFailureAsync(db, admin, now, http.RequestAborted);
                return BadRequest("Your current password is incorrect.");
            }

            admin.FailedLoginCount = 0;
            admin.LockedUntilUtc = null;

            if (request.NewPassword == request.CurrentPassword)
            {
                await db.SaveChangesAsync(http.RequestAborted);
                return BadRequest("New password must be different from your current password.");
            }

            // The new password and the end of every session are saved together, or not at all.
            admin.PasswordHash = hasher.HashPassword(admin, request.NewPassword);
            await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
            await db.SaveChangesAsync(http.RequestAborted);
            await EndAllSessionsAsync(db, admin.Id, http.RequestAborted);
            await transaction.CommitAsync(http.RequestAborted);
        }
        finally
        {
            LoginGate.Release();
        }

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        // The new password invalidates every session, so the account's live views have to end too.
        await CloseViewsOfAsync(screens, id);
        return Results.NoContent();
    }

    private static IResult? LockedResponse(AdminAccount? admin, DateTimeOffset now, HttpContext http)
    {
        if (admin?.LockedUntilUtc is not { } lockedUntil || lockedUntil <= now)
        {
            return null;
        }

        var seconds = (int)Math.Ceiling((lockedUntil - now).TotalSeconds);
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(
            new RateLimitResponse($"Too many attempts. Try again in {seconds} seconds.", seconds),
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static async Task RecordFailureAsync(AppDbContext db, AdminAccount admin, DateTimeOffset now, CancellationToken cancellationToken)
    {
        admin.FailedLoginCount++;
        if (admin.FailedLoginCount >= MaxFailedLogins)
        {
            admin.LockedUntilUtc = now + LockDuration;
            admin.FailedLoginCount = 0;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task ResetFailuresAsync(AppDbContext db, AdminAccount admin, CancellationToken cancellationToken)
    {
        admin.FailedLoginCount = 0;
        admin.LockedUntilUtc = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    // Returns null for a wrong content type, an unknown charset, malformed JSON, a JSON null body or an oversized body.
    internal static async Task<T?> ReadBodyAsync<T>(HttpRequest request) where T : class
    {
        if (!request.HasJsonContentType())
        {
            return null;
        }

        try
        {
            return await request.ReadFromJsonAsync<T>(request.HttpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            // BadHttpRequestException covers a body over the request size limit.
            return null;
        }
    }

    internal static IResult InvalidRequest() => BadRequest("Invalid request.");

    private static bool VerifyPassword(IPasswordHasher<AdminAccount> hasher, AdminAccount? account, string password)
    {
        var result = hasher.VerifyHashedPassword(
            account ?? DummyAccount,
            account?.PasswordHash ?? DummyHash,
            password);
        return account is not null && result != PasswordVerificationResult.Failed;
    }

    private static ClaimsPrincipal BuildPrincipal(AdminAccount admin, int idleTimeoutMinutes)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, admin.Username),
                new Claim(AuthServiceExtensions.SessionVersionClaim, admin.SessionVersion.ToString(CultureInfo.InvariantCulture)),
                new Claim(AuthServiceExtensions.IdleTimeoutMinutesClaim, idleTimeoutMinutes.ToString(CultureInfo.InvariantCulture)),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    // A session that never idles out keeps a persistent cookie, so it survives a browser restart until the admin signs out.
    // A session with an idle limit keeps a browser-session cookie whose expiry comes from the cookie options.
    private static AuthenticationProperties? BuildSessionProperties(int idleTimeoutMinutes, DateTimeOffset now) =>
        idleTimeoutMinutes == ServerSettings.NeverIdleTimeoutMinutes
            ? new AuthenticationProperties { IsPersistent = true, IssuedUtc = now, ExpiresUtc = now + AuthServiceExtensions.NeverIdleCookieLifetime }
            : null;

    internal static IResult BadRequest(string message) =>
        Results.Json(new ErrorResponse(message), statusCode: StatusCodes.Status400BadRequest);

    private static IResult WrongCredentials() =>
        Results.Json(new ErrorResponse("Wrong username or password."), statusCode: StatusCodes.Status401Unauthorized);

    internal static IResult SignInRequired() =>
        Results.Json(new ErrorResponse("Please sign in."), statusCode: StatusCodes.Status401Unauthorized);
}
