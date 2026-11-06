using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using LabGuard.Server.Data;
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
        group.MapPost("/logout", (Delegate)LogoutAsync);
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
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, BuildPrincipal(admin, settings.IdleTimeoutMinutes));
            return Results.Ok(new UserResponse(admin.Username));
        }
        finally
        {
            LoginGate.Release();
        }
    }

    private static async Task<IResult> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static IResult Me(ClaimsPrincipal user) =>
        Results.Ok(new UserResponse(user.Identity?.Name ?? ""));

    private static async Task<IResult> ChangePasswordAsync(
        HttpContext http,
        ClaimsPrincipal user,
        AppDbContext db,
        IPasswordHasher<AdminAccount> hasher,
        TimeProvider clock)
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

            admin.PasswordHash = hasher.HashPassword(admin, request.NewPassword);
            admin.SessionVersion++;
            await db.SaveChangesAsync(http.RequestAborted);
        }
        finally
        {
            LoginGate.Release();
        }

        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
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

    internal static IResult BadRequest(string message) =>
        Results.Json(new ErrorResponse(message), statusCode: StatusCodes.Status400BadRequest);

    private static IResult WrongCredentials() =>
        Results.Json(new ErrorResponse("Wrong username or password."), statusCode: StatusCodes.Status401Unauthorized);

    private static IResult SignInRequired() =>
        Results.Json(new ErrorResponse("Please sign in."), statusCode: StatusCodes.Status401Unauthorized);
}
