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
    // Sent by the dashboard's automatic polling so it doesn't count as activity for the idle timeout.
    public const string BackgroundRefreshHeader = "X-Background-Refresh";
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

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
                options.ExpireTimeSpan = IdleTimeout;
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

        if (isValid)
        {
            // Re-issue on every request so the 30 minutes are measured from the last activity,
            // not from the half-way point the sliding expiry would otherwise wait for.
            // Background refreshes skip this, so they can't keep an idle session alive.
            context.ShouldRenew = context.Request.Headers[BackgroundRefreshHeader] != "1";
        }
        else
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
