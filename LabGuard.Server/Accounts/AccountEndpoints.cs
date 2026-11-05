using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LabGuard.Server.Auth;
using LabGuard.Server.Data;
using LabGuard.Server.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Accounts;

// Minutes is a double so a fractional value reaches validation instead of failing JSON parsing.
public record DefaultTimeRequest(string? Mode, double? Minutes);
public record CreateAccountRequest(string? Username, string? Password, DefaultTimeRequest? DefaultTime);
public record UpdateAccountRequest(string? Username, string? Password, DefaultTimeRequest? DefaultTime);
public record AccountStatusRequest(bool? IsActive);

public record DefaultTimeResponse(
    string Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Minutes);

public record AccountResponse(
    int Id,
    string Username,
    bool IsActive,
    DefaultTimeResponse DefaultTime,
    DateTimeOffset? LastLoginAt);

public static partial class AccountEndpoints
{
    public const int MaxUsernameLength = 64;
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    public const int MinDefaultTimeMinutes = 1;
    public const int MaxDefaultTimeMinutes = 1440;
    public const string TimeLimitMode = "time-limit";
    public const string OpenTimeMode = "open-time";

    private const string UsernameTakenMessage = "That username is already taken.";
    private const int SqliteUniqueConstraintErrorCode = 2067;

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\z")]
    private static partial Regex UsernamePattern();

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts");
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id:int}", UpdateAsync);
        group.MapPatch("/{id:int}/status", SetStatusAsync);
        group.MapDelete("/{id:int}", DeleteAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var accounts = await db.UserAccounts
            .AsNoTracking()
            .OrderBy(u => u.Username)
            .ThenBy(u => u.Id)
            .ToListAsync(cancellationToken);
        return Results.Ok(accounts.Select(ToResponse));
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        AppDbContext db,
        IPasswordHasher<UserAccount> hasher)
    {
        var request = await AuthEndpoints.ReadBodyAsync<CreateAccountRequest>(http.Request);
        if (request is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        if (ValidateUsername(request.Username, out var username) is { } usernameError)
        {
            return AuthEndpoints.BadRequest(usernameError);
        }

        if (request.Password is null)
        {
            return AuthEndpoints.BadRequest("Enter a password.");
        }

        if (ValidatePassword(request.Password) is { } passwordError)
        {
            return AuthEndpoints.BadRequest(passwordError);
        }

        if (ValidateDefaultTime(request.DefaultTime, out var minutes) is { } timeError)
        {
            return AuthEndpoints.BadRequest(timeError);
        }

        var ct = http.RequestAborted;
        if (await db.UserAccounts.AnyAsync(u => u.Username == username, ct))
        {
            return UsernameTaken();
        }

        var account = new UserAccount { Username = username, IsActive = true, DefaultTimeMinutes = minutes };
        account.PasswordHash = hasher.HashPassword(account, request.Password);
        db.UserAccounts.Add(account);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return UsernameTaken();
        }

        return Results.Json(ToResponse(account), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(
        int id,
        HttpContext http,
        AppDbContext db,
        IPasswordHasher<UserAccount> hasher)
    {
        var request = await AuthEndpoints.ReadBodyAsync<UpdateAccountRequest>(http.Request);
        if (request is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        if (ValidateUsername(request.Username, out var username) is { } usernameError)
        {
            return AuthEndpoints.BadRequest(usernameError);
        }

        var changesPassword = !string.IsNullOrEmpty(request.Password);
        if (changesPassword && ValidatePassword(request.Password!) is { } passwordError)
        {
            return AuthEndpoints.BadRequest(passwordError);
        }

        if (ValidateDefaultTime(request.DefaultTime, out var minutes) is { } timeError)
        {
            return AuthEndpoints.BadRequest(timeError);
        }

        var ct = http.RequestAborted;
        var account = await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (account is null)
        {
            return NotFound();
        }

        if (await db.UserAccounts.AnyAsync(u => u.Id != id && u.Username == username, ct))
        {
            return UsernameTaken();
        }

        account.Username = username;
        account.DefaultTimeMinutes = minutes;
        if (changesPassword)
        {
            account.PasswordHash = hasher.HashPassword(account, request.Password!);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return NotFound();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return UsernameTaken();
        }

        return Results.Ok(ToResponse(account));
    }

    private static async Task<IResult> SetStatusAsync(int id, HttpContext http, AppDbContext db, TimeProvider clock)
    {
        var request = await AuthEndpoints.ReadBodyAsync<AccountStatusRequest>(http.Request);
        if (request is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        if (request.IsActive is not { } isActive)
        {
            return AuthEndpoints.BadRequest("Specify whether the account is active.");
        }

        var ct = http.RequestAborted;
        var account = await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (account is null)
        {
            return NotFound();
        }

        account.IsActive = isActive;
        // Disabling and ending the account's sessions happen together. Enabling never revives them.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
            if (!isActive)
            {
                await SessionLifecycle.EndForAccountAsync(db, id, SessionLifecycle.AccountDisabled, clock.GetUtcNow(), ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return NotFound();
        }

        return Results.Ok(ToResponse(account));
    }

    private static async Task<IResult> DeleteAsync(int id, AppDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var account = await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (account is null)
        {
            return NotFound();
        }

        db.UserAccounts.Remove(account);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await SessionLifecycle.EndForAccountAsync(db, id, SessionLifecycle.AccountDeleted, clock.GetUtcNow(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return NotFound();
        }

        return Results.NoContent();
    }

    // Returns an error message, or null when valid; the trimmed username comes back through the out parameter.
    private static string? ValidateUsername(string? raw, out string username)
    {
        username = raw?.Trim() ?? "";
        if (username.Length == 0)
        {
            return "Enter a username.";
        }

        if (username.Length > MaxUsernameLength || !UsernamePattern().IsMatch(username))
        {
            return $"Username must be 1 to {MaxUsernameLength} characters: letters, numbers, dots, underscores and hyphens only.";
        }

        return null;
    }

    private static string? ValidatePassword(string password)
    {
        if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
        {
            return $"Password must be {MinPasswordLength} to {MaxPasswordLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            return "Password can't be only spaces.";
        }

        return null;
    }

    internal const string MissingDefaultTimeMessage = "Choose a default time.";

    // Shared with login request approval, which passes its own wording for the missing and unknown-mode cases.
    internal static string? ValidateDefaultTime(
        DefaultTimeRequest? defaultTime,
        out int? minutes,
        string missingMessage = MissingDefaultTimeMessage,
        string? invalidModeMessage = null)
    {
        minutes = null;
        if (defaultTime is null)
        {
            return missingMessage;
        }

        switch (defaultTime.Mode)
        {
            case OpenTimeMode:
                return defaultTime.Minutes is null ? null : "Open time can't have a number of minutes.";

            case TimeLimitMode:
                if (defaultTime.Minutes is not { } value
                    || !double.IsFinite(value)
                    || value != Math.Floor(value)
                    || value < MinDefaultTimeMinutes
                    || value > MaxDefaultTimeMinutes)
                {
                    return $"Time limit must be a whole number of minutes from {MinDefaultTimeMinutes} to {MaxDefaultTimeMinutes}.";
                }

                minutes = (int)value;
                return null;

            default:
                return invalidModeMessage ?? $"Default time mode must be \"{TimeLimitMode}\" or \"{OpenTimeMode}\".";
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteUniqueConstraintErrorCode };

    private static AccountResponse ToResponse(UserAccount account) =>
        new(
            account.Id,
            account.Username,
            account.IsActive,
            account.DefaultTimeMinutes is { } minutes
                ? new DefaultTimeResponse(TimeLimitMode, minutes)
                : new DefaultTimeResponse(OpenTimeMode, null),
            account.LastLoginUtc);

    private static IResult UsernameTaken() =>
        Results.Json(new ErrorResponse(UsernameTakenMessage), statusCode: StatusCodes.Status409Conflict);

    private static IResult NotFound() =>
        Results.Json(new ErrorResponse("Account not found."), statusCode: StatusCodes.Status404NotFound);
}
