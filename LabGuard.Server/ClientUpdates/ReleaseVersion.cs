using System.Globalization;

namespace LabGuard.Server.ClientUpdates;

// A "major.minor.patch" client version. Compared as numbers, so 0.9.0 is older than 0.10.0.
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    public int CompareTo(ReleaseVersion other) =>
        (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    // Exactly three non-negative whole numbers, nothing else.
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        var parts = text?.Split('.');
        if (parts is { Length: 3 } && TryParseParts(parts, out version))
        {
            return true;
        }

        version = default;
        return false;
    }

    // For a version already stored by this server, which was validated when it was written.
    public static ReleaseVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a major.minor.patch version.");

    // A version resource has up to four parts ("0.19.0.0"). Only the first three count.
    public static bool TryParseFileVersion(string? text, out ReleaseVersion version)
    {
        var parts = text?.Split('.');
        if (parts is { Length: >= 3 } && TryParseParts(parts[..3], out version))
        {
            return true;
        }

        version = default;
        return false;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    // Digits only, and each part must fit an int. int.TryParse reports the overflow, so a huge part is refused, not wrapped.
    private static bool TryParseParts(string[] parts, out ReleaseVersion version)
    {
        version = default;
        if (parts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit))
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch);
        return true;
    }
}
