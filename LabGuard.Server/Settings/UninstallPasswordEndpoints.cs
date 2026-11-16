using System.Text.Json;
using LabGuard.Server.Auth;
using LabGuard.Server.Common;
using LabGuard.Server.Data;

namespace LabGuard.Server.Settings;

public record UninstallPasswordStatus(bool IsSet);

// Password is a JsonElement so a JSON null (clear the password) can be told apart from a missing field (refused).
public record UninstallPasswordRequest(JsonElement Password);

// Only tells whether a password is set. The hash is never sent to the admin page, only to the PCs.
public static class UninstallPasswordEndpoints
{
    private const string UnreadableCharacterError = "The uninstall password can't contain a character that can't be typed.";

    public static RouteGroupBuilder MapUninstallPasswordEndpoints(this RouteGroupBuilder settingsGroup)
    {
        settingsGroup.MapGet("/uninstall-password", GetAsync);
        settingsGroup.MapPut("/uninstall-password", SaveAsync);
        return settingsGroup;
    }

    private static async Task<IResult> GetAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var settings = await ServerSettingsStore.GetAsync(db, cancellationToken);
        return Results.Ok(StatusOf(settings));
    }

    private static async Task<IResult> SaveAsync(HttpContext http, AppDbContext db)
    {
        var request = await AuthEndpoints.ReadBodyAsync<UninstallPasswordRequest>(http.Request);
        if (request is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        if (TryReadPassword(request.Password, out var password) is { } error)
        {
            return AuthEndpoints.BadRequest(error);
        }

        var hash = password is null ? null : UninstallPassword.Hash(password);
        var ct = http.RequestAborted;
        var settings = await ServerSettingsStore.GetAsync(db, ct);
        UninstallPassword.Store(settings, hash);
        await db.SaveChangesAsync(ct);
        return Results.Ok(StatusOf(settings));
    }

    // Returns an error message, or null when valid; the password comes back through the out parameter, null to clear it.
    // The password is checked as typed, never trimmed, so the PC hashes exactly what the administrator chose.
    private static string? TryReadPassword(JsonElement value, out string? password)
    {
        password = null;
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return "Enter an uninstall password, or null to remove it.";
        }

        try
        {
            password = value.GetString()!;
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate escape can't be read as text.
            return UnreadableCharacterError;
        }

        // A lone surrogate has no UTF-8 form, so the PC could never hash it the same way. Checked before normalizing, which throws for one.
        if (UnpairedSurrogates.IsPresentIn(password))
        {
            return UnreadableCharacterError;
        }

        // The length counts the normalized password, which is what gets hashed.
        if (UninstallPassword.Normalize(password).Length is < UninstallPassword.MinLength or > UninstallPassword.MaxLength)
        {
            return $"The uninstall password must be {UninstallPassword.MinLength} to {UninstallPassword.MaxLength} characters.";
        }

        return null;
    }

    private static UninstallPasswordStatus StatusOf(ServerSettings settings) =>
        new(UninstallPassword.StoredIn(settings) is not null);
}
