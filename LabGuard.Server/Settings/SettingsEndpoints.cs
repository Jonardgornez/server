using LabGuard.Server.Accounts;
using LabGuard.Server.Auth;
using LabGuard.Server.Common;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.Settings;

public record ApprovalSettings(string Mode, int Minutes);
public record NoticeSettings(bool IsEnabled, string Text);
public record SettingsResponse(
    ApprovalSettings DefaultApproval,
    NoticeSettings Notice,
    int IdleTimeoutMinutes,
    string DefaultViewMode,
    int PowerCountdownSeconds,
    string LockMessage);

// Every member is nullable, so a missing field gets a clear 400 instead of a binding failure.
// Minutes is a double so a fractional value reaches validation instead of failing JSON parsing.
public record ApprovalRequest(string? Mode, double? Minutes);
public record NoticeRequest(bool? IsEnabled, string? Text);
public record SettingsRequest(
    ApprovalRequest? DefaultApproval,
    NoticeRequest? Notice,
    double? IdleTimeoutMinutes,
    string? DefaultViewMode,
    double? PowerCountdownSeconds,
    string? LockMessage);

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings");
        group.MapGet("/", GetAsync);
        group.MapPut("/", SaveAsync);
        return app;
    }

    private static async Task<IResult> GetAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var settings = await ServerSettingsStore.GetAsync(db, cancellationToken);
        return Results.Ok(ToResponse(settings));
    }

    private static async Task<IResult> SaveAsync(HttpContext http, AppDbContext db)
    {
        var request = await AuthEndpoints.ReadBodyAsync<SettingsRequest>(http.Request);
        if (request is null)
        {
            return AuthEndpoints.InvalidRequest();
        }

        if (TryBuild(request, out var validated) is { } error)
        {
            return AuthEndpoints.BadRequest(error);
        }

        var ct = http.RequestAborted;
        var settings = await ServerSettingsStore.GetAsync(db, ct);
        db.Entry(settings).CurrentValues.SetValues(validated);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(settings));
    }

    // Returns an error message, or null when valid; the checked settings come back through the out parameter.
    private static string? TryBuild(SettingsRequest request, out ServerSettings settings)
    {
        settings = new ServerSettings();

        if (request.DefaultApproval is not { } approval)
        {
            return "Choose a default approval.";
        }

        if (approval.Mode is not (AccountEndpoints.TimeLimitMode or AccountEndpoints.OpenTimeMode))
        {
            return $"Approval mode must be \"{AccountEndpoints.TimeLimitMode}\" or \"{AccountEndpoints.OpenTimeMode}\".";
        }

        if (WholeNumberOrNull(approval.Minutes, AccountEndpoints.MinDefaultTimeMinutes, AccountEndpoints.MaxDefaultTimeMinutes) is not { } approvalMinutes)
        {
            return $"Approval minutes must be a whole number from {AccountEndpoints.MinDefaultTimeMinutes} to {AccountEndpoints.MaxDefaultTimeMinutes}.";
        }

        if (request.Notice is not { IsEnabled: { } isEnabled } notice)
        {
            return "Choose whether the notice is on.";
        }

        var noticeText = notice.Text?.Trim() ?? "";
        if (UnpairedSurrogates.IsPresentIn(noticeText))
        {
            return "The notice can't contain a character that can't be shown.";
        }

        if (noticeText.Length > ServerSettings.MaxNoticeLength)
        {
            return $"Notice must be {ServerSettings.MaxNoticeLength} characters or fewer.";
        }

        if (isEnabled && noticeText.Length == 0)
        {
            return "Enter the notice text, or turn the notice off.";
        }

        var lockMessage = request.LockMessage?.Trim() ?? "";
        if (lockMessage.Length == 0)
        {
            return "Enter the lock screen message.";
        }

        if (UnpairedSurrogates.IsPresentIn(lockMessage) || lockMessage.Any(char.IsControl))
        {
            return "The lock screen message can't contain a character that can't be shown.";
        }

        if (lockMessage.Length > ServerSettings.MaxLockMessageLength)
        {
            return $"The lock screen message must be {ServerSettings.MaxLockMessageLength} characters or fewer.";
        }

        if (WholeNumberOrNull(request.IdleTimeoutMinutes, ServerSettings.MinIdleTimeoutMinutes, ServerSettings.MaxIdleTimeoutMinutes) is not { } idleMinutes)
        {
            return $"Idle timeout must be a whole number of minutes from {ServerSettings.MinIdleTimeoutMinutes} to {ServerSettings.MaxIdleTimeoutMinutes}.";
        }

        if (WholeNumberOrNull(request.PowerCountdownSeconds, ServerSettings.MinPowerCountdownSeconds, ServerSettings.MaxPowerCountdownSeconds) is not { } powerCountdown)
        {
            return $"Power countdown must be a whole number of seconds from {ServerSettings.MinPowerCountdownSeconds} to {ServerSettings.MaxPowerCountdownSeconds}.";
        }

        if (request.DefaultViewMode is not (ServerSettings.NotifiedViewMode or ServerSettings.AnonymousViewMode))
        {
            return $"Default view mode must be \"{ServerSettings.NotifiedViewMode}\" or \"{ServerSettings.AnonymousViewMode}\".";
        }

        settings.DefaultApprovalMode = approval.Mode;
        settings.DefaultApprovalMinutes = approvalMinutes;
        settings.NoticeEnabled = isEnabled;
        settings.NoticeText = noticeText;
        settings.IdleTimeoutMinutes = idleMinutes;
        settings.PowerCountdownSeconds = powerCountdown;
        settings.DefaultViewMode = request.DefaultViewMode;
        settings.LockMessage = lockMessage;
        return null;
    }

    private static int? WholeNumberOrNull(double? value, int min, int max) =>
        value is { } number && double.IsFinite(number) && number == Math.Floor(number) && number >= min && number <= max
            ? (int)number
            : null;

    private static SettingsResponse ToResponse(ServerSettings settings) =>
        new(
            new ApprovalSettings(settings.DefaultApprovalMode, settings.DefaultApprovalMinutes),
            new NoticeSettings(settings.NoticeEnabled, settings.NoticeText),
            settings.IdleTimeoutMinutes,
            settings.DefaultViewMode,
            settings.PowerCountdownSeconds,
            settings.LockMessage);
}
