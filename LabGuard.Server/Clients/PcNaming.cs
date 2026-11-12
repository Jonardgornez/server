using System.Globalization;
using System.Text.RegularExpressions;

namespace LabGuard.Server.Clients;

// The server names every newly registered PC: PC-1, PC-2, PC-3, and so on. The lowest number no PC or waiting request uses is
// taken, so a removed PC's number is given to the next PC that registers and the list stays in order with no gaps.
public static partial class PcNaming
{
    public const string Prefix = "PC-";

    // The numbers a PC-<number> name can have. They match the regex below: no leading zero and at most nine digits.
    public const int MinNumber = 1;
    public const int MaxNumber = 999_999_999;

    public static bool IsValidNumber(int number) => number is >= MinNumber and <= MaxNumber;

    // The name for a number, such as PC-9. The number must be in range.
    public static string NameFor(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, MinNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, MaxNumber);
        return Prefix + number.ToString(CultureInfo.InvariantCulture);
    }

    // Only names shaped like PC-<number> count. Any other name, such as one typed by hand before automatic names, is ignored.
    [GeneratedRegex(@"^PC-([1-9][0-9]{0,8})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedName();

    // The lowest PC-<number> that none of the used names takes. Case is ignored, so "pc-3" also takes number 3.
    public static string NextFreeName(IEnumerable<string> usedNames)
    {
        var taken = new HashSet<int>();
        foreach (var name in usedNames)
        {
            var match = NumberedName().Match(name);
            if (match.Success)
            {
                taken.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }
        }

        var number = 1;
        while (taken.Contains(number))
        {
            number++;
        }

        return Prefix + number.ToString(CultureInfo.InvariantCulture);
    }

    // Orders names the way a person reads them: PC-1, PC-2, ..., PC-9, PC-10, PC-11. A name is split into its text and the
    // number it ends with. The text is compared ignoring case, then the number by value, so PC-2 comes before PC-10.
    public static readonly IComparer<string> DisplayOrder = Comparer<string>.Create(CompareForDisplay);

    private static int CompareForDisplay(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;

        var (leftText, leftDigits) = SplitTrailingNumber(left);
        var (rightText, rightDigits) = SplitTrailingNumber(right);

        var byText = string.Compare(leftText, rightText, StringComparison.OrdinalIgnoreCase);
        if (byText != 0) return byText;

        // A name with no number comes before the same text with one, so "PC" sorts before "PC1".
        var byNumber = CompareDigits(leftDigits, rightDigits);
        if (byNumber != 0) return byNumber;

        // Same text and value, such as PC-1 and pc-01: fall back to an exact order so the list never shuffles.
        return string.CompareOrdinal(left, right);
    }

    private static (string Text, string Digits) SplitTrailingNumber(string name)
    {
        var start = name.Length;
        while (start > 0 && char.IsAsciiDigit(name[start - 1]))
        {
            start--;
        }

        return (name[..start], name[start..]);
    }

    // Compares two runs of ASCII digits by value without parsing them, so a number of any length can't overflow.
    private static int CompareDigits(string left, string right)
    {
        var leftValue = left.TrimStart('0');
        var rightValue = right.TrimStart('0');
        if (left.Length == 0 || right.Length == 0) return left.Length.CompareTo(right.Length);
        if (leftValue.Length != rightValue.Length) return leftValue.Length.CompareTo(rightValue.Length);
        return string.CompareOrdinal(leftValue, rightValue);
    }
}
