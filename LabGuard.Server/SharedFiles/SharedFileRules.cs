using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LabGuard.Server.Common;

namespace LabGuard.Server.SharedFiles;

// Rules for files the administrator sends to PCs: which names and folders are accepted, the statuses a transfer goes through,
// and how long things are kept. The PC checks names and folders with the same rules before it writes anything.
public static partial class SharedFileRules
{
    public const int MaxSubfolderSegments = 3;
    public const int MaxSegmentLength = 64;
    public const int MaxFileNameLength = 200;
    // The name a PC saved under can be a little longer than the sent name, since "keep both" adds " (2)" and so on.
    public const int MaxSavedAsLength = 255;
    public const int MaxErrorLength = 200;
    public const int MaxPcsPerSend = 200;
    public const int MaxTransfersPerRequest = 200;
    // One at a time per PC: a PC is handed a transfer only when it is working on none (see the heartbeat hand-over).
    public const int MaxHandOverPerHeartbeat = 1;

    public const string DefaultFolder = DesktopFolder;
    public const string DefaultSubfolder = "Lykantra Share";
    public const string DesktopFolder = "desktop";
    public const string DocumentsFolder = "documents";
    public const string DownloadsFolder = "downloads";

    public const string Waiting = "waiting";
    public const string Downloading = "downloading";
    public const string Exists = "exists";
    public const string Applying = "applying";
    public const string Saved = "saved";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
    public const string Expired = "expired";

    public const string Overwrite = "overwrite";
    public const string KeepBoth = "keepBoth";
    public const string Skip = "skip";

    public const string InvalidFileNameMessage = "Choose a file with a valid Windows file name.";
    public const string InvalidSubfolderMessage =
        "Enter a subfolder of up to 3 folder names, each 1 to 64 characters, without < > : \" / | ? * or a reserved Windows name.";
    public const string StoppedRespondingError = "PC stopped responding";
    public const string DefaultFailedError = "The PC couldn't save the file.";

    // A transfer still waiting this long after it was sent is never handed over, and shows as expired.
    public static readonly TimeSpan WaitingLifetime = TimeSpan.FromHours(1);
    // A PC that has been downloading or applying a file this long has stopped, so the transfer fails.
    public static readonly TimeSpan ActiveLifetime = TimeSpan.FromHours(1);
    // Transfers are kept this long after they were created, then removed.
    public static readonly TimeSpan TransferLifetime = TimeSpan.FromHours(24);
    // A shared file no transfer still needs is kept this long after its last upload or send, then its bytes are removed.
    public static readonly TimeSpan UnusedFileLifetime = TimeSpan.FromHours(1);

    // The statuses in which the PC may still need the file's bytes.
    public static readonly string[] OpenStatuses = [Waiting, Downloading, Exists, Applying];

    private static readonly string[] Folders = [DesktopFolder, DocumentsFolder, DownloadsFolder];
    private static readonly string[] Decisions = [Overwrite, KeepBoth, Skip];
    private static readonly char[] Separators = ['\\', '/'];
    private static readonly char[] ForbiddenChars = ['<', '>', ':', '"', '|', '?', '*', '/', '\\'];

    // Windows device names, the same list the PC and the dashboard refuse. The superscript digits are device names on Windows too.
    private static readonly HashSet<string> ReservedNames = new(
        new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }
            .Concat("0123456789¹²³".SelectMany(digit => new[] { $"COM{digit}", $"LPT{digit}" })),
        StringComparer.OrdinalIgnoreCase);

    // The folder settings file Explorer reads. Writing one could change how a folder shows, so it is never a name.
    private const string DesktopIni = "desktop.ini";

    // The longest part before the extension an 8.3 short name can have.
    private const int MaxShortNameBaseLength = 8;

    // The shape of an 8.3 short name such as REPORT~1.PDF or LYKANT~1. It can stand for a different, existing long name on the PC.
    // [0-9], not \d: only ASCII digits make a short name.
    [GeneratedRegex(@"^[^.~]{1,6}~[0-9]{1,6}(\.[^.]{1,3})?\z", RegexOptions.IgnoreCase)]
    private static partial Regex ShortNamePattern();

    public static bool IsKnownFolder(string folder) => Folders.Contains(folder, StringComparer.Ordinal);

    public static bool IsDecision(string decision) => Decisions.Contains(decision, StringComparer.Ordinal);

    // One folder or file name: 1 to maxLength characters, not "." or "..", none of the characters Windows refuses, no control
    // characters (C0, DEL and C1) or invisible format characters (such as direction marks, checked per code point), no leading
    // space, no trailing dot or space, no unpaired surrogate, not desktop.ini, not an 8.3 short name, and not a device name with
    // or without an extension.
    public static bool IsValidSegment(string segment, int maxLength)
    {
        if (segment.Length == 0 || segment.Length > maxLength || segment is "." or "..")
        {
            return false;
        }

        if (segment.IndexOfAny(ForbiddenChars) >= 0 || segment.Any(IsControl) || segment.EnumerateRunes().Any(IsFormat))
        {
            return false;
        }

        if (segment[0] == ' ' || segment[^1] is '.' or ' ' || UnpairedSurrogates.IsPresentIn(segment))
        {
            return false;
        }

        if (segment.Equals(DesktopIni, StringComparison.OrdinalIgnoreCase) || IsShortName(segment))
        {
            return false;
        }

        // Windows ignores the extension and trailing spaces when it reads a device name: "CON.txt" and "CON .txt" are CON.
        var stem = segment.Split('.')[0].TrimEnd(' ');
        return !ReservedNames.Contains(stem);
    }

    // Returns true with the subfolder as stored and sent: its names joined with "\". Null means the default subfolder and an
    // empty string means none. Anything rooted fails, since its first name is empty or holds a drive's ":".
    public static bool TryNormalizeSubfolder(string? raw, out string subfolder)
    {
        subfolder = "";
        if (raw is null)
        {
            subfolder = DefaultSubfolder;
            return true;
        }

        if (raw.Length == 0)
        {
            return true;
        }

        var segments = raw.Split(Separators);
        if (segments.Length > MaxSubfolderSegments || !segments.All(segment => IsValidSegment(segment, MaxSegmentLength)))
        {
            return false;
        }

        subfolder = string.Join('\\', segments);
        return true;
    }

    // Returns true with the uploaded file's name: only its last path part, which must pass the name rules.
    public static bool TryCleanFileName(string? raw, out string fileName)
    {
        fileName = "";
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        var lastPart = raw[(raw.LastIndexOfAny(Separators) + 1)..];
        if (!IsValidSegment(lastPart, MaxFileNameLength))
        {
            return false;
        }

        fileName = lastPart;
        return true;
    }

    // The PC's reason for a failure, as stored: control and format characters removed, trimmed and cut to the limit. A blank reason gets a default.
    public static string CleanError(string? raw)
    {
        var kept = new StringBuilder();
        foreach (var rune in (raw ?? "").EnumerateRunes())
        {
            if (!Rune.IsControl(rune) && !IsFormat(rune))
            {
                kept.Append(rune.ToString());
            }
        }

        var cleaned = kept.ToString().Trim();
        if (cleaned.Length > MaxErrorLength)
        {
            cleaned = cleaned[..MaxErrorLength].TrimEnd();
            if (cleaned.Length > 0 && char.IsHighSurrogate(cleaned[^1]))
            {
                cleaned = cleaned[..^1];
            }
        }

        return cleaned.Length == 0 ? DefaultFailedError : cleaned;
    }

    // C0 controls, DEL and C1 controls: exactly what char.IsControl covers.
    private static bool IsControl(char c) => char.IsControl(c);

    // Per code point, so a format character outside the BMP (such as U+E0001, LANGUAGE TAG) is caught too.
    private static bool IsFormat(Rune rune) => Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format;

    // Has the shape of an 8.3 short name, with at most 8 characters before the extension.
    internal static bool IsShortName(string segment)
    {
        if (!ShortNamePattern().IsMatch(segment))
        {
            return false;
        }

        var dot = segment.IndexOf('.');
        return (dot < 0 ? segment.Length : dot) <= MaxShortNameBaseLength;
    }

    // A waiting transfer that was never handed over within its lifetime shows as expired, whether or not the cleanup has stored that yet.
    public static string EffectiveStatus(string status, DateTimeOffset createdUtc, DateTimeOffset now) =>
        status == Waiting && now - createdUtc >= WaitingLifetime ? Expired : status;

    // The statuses a PC may move a transfer out of to reach the reported status. Empty for a status a PC can't report.
    public static string[] AllowedFrom(string reportedStatus) => reportedStatus switch
    {
        Saved => [Downloading, Applying],
        Failed => [Downloading, Applying],
        Exists => [Downloading],
        Skipped => [Applying],
        _ => [],
    };
}
