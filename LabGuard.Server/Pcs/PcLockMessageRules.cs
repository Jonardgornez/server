using LabGuard.Server.Common;
using LabGuard.Server.Data;

namespace LabGuard.Server.Pcs;

// The optional message an administrator types when locking one or more PCs. It is shown on the locked PC in place of the setting.
public static class PcLockMessageRules
{
    // Returns true with the message as it is stored: trimmed, or null when there is no message and the setting's message is used.
    // Otherwise returns false with the reason for the administrator.
    public static bool TryCheck(string? raw, out string? message, out string error)
    {
        message = null;
        error = "";
        var trimmed = raw?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.Length > ServerSettings.MaxLockMessageLength)
        {
            error = $"The message must be {ServerSettings.MaxLockMessageLength} characters or fewer.";
        }
        else if (trimmed.Any(char.IsControl))
        {
            error = "The message can't contain line breaks or control characters.";
        }
        else if (UnpairedSurrogates.IsPresentIn(trimmed))
        {
            error = "The message can't contain a character that can't be shown.";
        }
        else
        {
            message = trimmed;
        }

        return error.Length == 0;
    }
}
