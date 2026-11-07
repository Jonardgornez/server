using LabGuard.Server.Common;

namespace LabGuard.Server.Pcs;

// Rules for messages an administrator sends to PCs: what text is accepted, how many PCs one send may name, and the timings of delivery.
public static class PcMessageRules
{
    public const int MaxTextLength = 500;
    public const int MaxLines = 10;
    public const int MaxRecipients = 100;
    public const int MaxUndeliveredPerPc = 5;
    public const int MaxHandOverPerHeartbeat = 5;

    public const string DeliveredStatus = "delivered";
    public const string FailedStatus = "failed";
    public const string OfflineStatus = "offline";
    public const string NotFoundStatus = "not-found";
    public const string BusyStatus = "busy";

    // A message not acknowledged within this time of being created has failed. It is never handed over after that.
    public static readonly TimeSpan DeliveryWindow = TimeSpan.FromSeconds(6);

    // A PC may acknowledge a message this long after it was created. Only an acknowledgement inside the delivery window counts as delivered.
    public static readonly TimeSpan AckWindow = TimeSpan.FromSeconds(60);

    // Created no later than now and less than the delivery window old. A message created in the future, by a clock moved
    // back, is not fresh, so it is neither handed over nor counted as pending.
    public static bool IsInDeliveryWindow(DateTimeOffset createdUtc, DateTimeOffset now) =>
        now >= createdUtc && now - createdUtc < DeliveryWindow;

    // Not older than the ack window. A time in the future counts as recent, so a clock moved back never refuses an acknowledgement.
    public static bool IsInAckWindow(DateTimeOffset createdUtc, DateTimeOffset now) => now - createdUtc <= AckWindow;

    // Null when the recipients are acceptable, otherwise the reason for the administrator.
    public static string? RecipientsError(IReadOnlyCollection<int> pcIds)
    {
        if (pcIds.Count == 0)
        {
            return "Choose at least one PC.";
        }

        if (pcIds.Count > MaxRecipients)
        {
            return $"Choose at most {MaxRecipients} PCs.";
        }

        return pcIds.Distinct().Count() == pcIds.Count ? null : "Each PC can be chosen only once.";
    }

    // Returns true with the text as it is stored. Line endings are made plain \n first, then the text is trimmed.
    // Otherwise returns false with the reason for the administrator.
    public static bool TryCheckText(string? raw, out string text, out string error)
    {
        text = "";
        error = "";
        var normalized = raw?.Replace("\r\n", "\n").Replace('\r', '\n').Trim() ?? "";
        if (normalized.Length == 0)
        {
            error = "Enter a message.";
        }
        else if (normalized.Length > MaxTextLength)
        {
            error = $"A message can be at most {MaxTextLength} characters.";
        }
        else if (normalized.Count(c => c == '\n') + 1 > MaxLines)
        {
            error = $"A message can be at most {MaxLines} lines.";
        }
        else if (normalized.Any(c => c != '\n' && char.IsControl(c)))
        {
            // char.IsControl covers exactly U+0000 to U+001F and U+007F to U+009F. A line break is the one control character kept.
            error = "A message can't contain control characters.";
        }
        else if (normalized.Any(IsDirectionOrInvisibleFormatting))
        {
            error = "The message can't contain direction or invisible formatting characters.";
        }
        else if (UnpairedSurrogates.IsPresentIn(normalized))
        {
            error = "A message contains a character that can't be shown.";
        }
        else
        {
            text = normalized;
        }

        return error.Length == 0;
    }

    // Bidi controls, which can reorder the text on screen, and invisible separators and marks. Zero-width joiners and
    // non-joiners are not listed, since they are needed for some scripts and emoji. Other format characters are allowed.
    private static bool IsDirectionOrInvisibleFormatting(char c) =>
        c is (char)0x061C or (char)0x200B or (char)0x200E or (char)0x200F or (char)0x2028 or (char)0x2029 or (char)0xFEFF
            or (>= (char)0x202A and <= (char)0x202E)
            or (>= (char)0x2066 and <= (char)0x2069);
}
