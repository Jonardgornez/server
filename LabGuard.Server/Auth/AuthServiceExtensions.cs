using System.Globalization;
using System.Security.Claims;
using LabGuard.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Auth;

public static class AuthServiceExtensions
{
    public const string CookieName = "lykantra.admin";
    public const string SessionVersionClaim = "lykantra:session-version";
    // The idle limit that was saved when this session signed in. It applies to that session only.
    public const string IdleTimeoutMinutesClaim = "lykantra:idle-timeout-minutes";
    // Sent by the dashboard's automatic polling so it doesn't count as activity for the idle timeout.
    public const string BackgroundRefreshHeader = "X-Background-Refresh";

    public static IServiceCollection AddAdminAuth(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPasswordHasher<AdminAccount>, PasswordHasher<AdminAccount>>();
        services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                // The longest idle limit an administrator can save. The limit a session actually gets is checked in
                // ValidateSessionVersionAsync, so this only needs to be at least as long as any saved limit.
                options.ExpireTimeSpan = TimeSpan.FromMinutes(ServerSettings.MaxIdleTimeoutMinutes);
                // Renewal is explicit in ValidateSessionVersionAsync. The built-in sliding renewal would
                // also fire past the half-way point on background refreshes and keep an idle session alive.
                options.SlidingExpiration = false;
                options.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = ValidateSessionVersionAsync,
                    OnRedirectToLogin = context => WriteStatusAsync(context.Response, StatusCodes.Status401Unauthorized, "Please sign in."),
                    OnRedirectToAccessDenied = context => WriteStatusAsync(context.Response, StatusCodes.Status403Forbidden, "You do not have access to this resource."),
                };
            });

        // Let the cookie handler use the injected clock so expiry can be tested without sleeping.
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<TimeProvider>((options, timeProvider) => options.TimeProvider = timeProvider);

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }

    private static Task WriteStatusAsync(HttpResponse response, int statusCode, string message)
    {
        response.StatusCode = statusCode;
        return response.WriteAsJsonAsync(new ErrorResponse(message));
    }

    private static async Task ValidateSessionVersionAsync(CookieValidatePrincipalContext context)
    {
        var idText = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var versionText = context.Principal?.FindFirstValue(SessionVersionClaim);

        var isValid = false;
        if (int.TryParse(idText, CultureInfo.InvariantCulture, out var id)
            && int.TryParse(versionText, CultureInfo.InvariantCulture, out var version))
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var currentVersion = await db.AdminAccounts
                .Where(a => a.Id == id)
                .Select(a => (int?)a.SessionVersion)
                .FirstOrDefaultAsync(context.HttpContext.RequestAborted);
            isValid = currentVersion == version;
        }

        if (isValid && IsIdleTooLong(context))
        {
            isValid = false;
        }

        if (isValid)
        {
            // Re-issue on every request so the idle limit is measured from the last activity,
            // not from the half-way point the sliding expiry would otherwise wait for.
            // Background refreshes and endpoints marked NoSessionRenewal skip this, so they can't keep an idle session alive.
            context.ShouldRenew = context.Request.Headers[BackgroundRefreshHeader] != "1"
                && context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<NoSessionRenewalAttribute>() is null;
        }
        else
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    // Measured from the last request that renewed the session. A session with no saved limit is refused.
    private static bool IsIdleTooLong(CookieValidatePrincipalContext context)
    {
        var deadline = SessionDeadlineUtc(context.Principal, context.Properties);
        var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        return deadline is not { } endsAt || now > endsAt;
    }

    // When the session ends if nothing renews it: the idle limit counted from the last renewal, or the cookie's own expiry if that is sooner.
    // Null for a session with no saved limit or no issue time, which the caller refuses.
    public static DateTimeOffset? SessionDeadlineUtc(ClaimsPrincipal? principal, AuthenticationProperties? properties)
    {
        if (!int.TryParse(principal?.FindFirstValue(IdleTimeoutMinutesClaim), CultureInfo.InvariantCulture, out var idleMinutes)
            || properties?.IssuedUtc is not { } lastRenewedUtc)
        {
            return null;
        }

        var idleDeadline = lastRenewedUtc.AddMinutes(idleMinutes);
        return properties.ExpiresUtc is { } cookieExpiry && cookieExpiry < idleDeadline ? cookieExpiry : idleDeadline;
    }
}

// Marks an endpoint whose requests must not renew the admin session, such as the live screen view.
// A WebSocket can't send the background-refresh header, so this marker is what keeps a stream from extending the idle limit.
public sealed class NoSessionRenewalAttribute : Attribute;
