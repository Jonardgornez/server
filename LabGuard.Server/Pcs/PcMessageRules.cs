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

    // Less than the delivery window old. A message this old can still be handed over and delivered.
    public static bool IsInDeliveryWindow(DateTimeOffset createdUtc, DateTimeOffset now) => now - createdUtc < DeliveryWindow;

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
        else if (HasUnpairedSurrogate(normalized))
        {
            error = "A message contains a character that can't be shown.";
        }
        else
        {
            text = normalized;
        }

        return error.Length == 0;
    }

    private static bool HasUnpairedSurrogate(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsLowSurrogate(text[index]))
            {
                return true;
            }

            if (!char.IsHighSurrogate(text[index]))
            {
                continue;
            }

            if (index + 1 == text.Length || !char.IsLowSurrogate(text[index + 1]))
            {
                return true;
            }

            index++;
        }

        return false;
    }
}
