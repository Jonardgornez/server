using System.Globalization;
using System.Text.RegularExpressions;

namespace LabGuard.Server.Clients;

// The server names every newly registered PC: PC-1, PC-2, PC-3, and so on. The lowest number no PC or waiting request uses is
// taken, so a removed PC's number is given to the next PC that registers and the list stays in order with no gaps.
public static partial class PcNaming
{
    public const string Prefix = "PC-";

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
}
