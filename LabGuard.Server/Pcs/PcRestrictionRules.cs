namespace LabGuard.Server.Pcs;

// The Restrictions policies a PC's service applies for the signed-in user, and how a PC row stores them. Task Manager, Fast User
// Switching and file and folder protection are not among them: the first two have their own fields and routes, and the last is not built.
// Policies are stored comma-separated in the order of PolicyIds, and drives as their letters in order ("CD"), so equal choices store equally.
public static class PcRestrictionRules
{
    public static readonly IReadOnlyList<string> PolicyIds =
    [
        "registry-editor",
        "command-prompt",
        "run-command",
        "control-panel",
        "add-printer",
        "remove-printer",
        "windows-logout",
        "windows-shutdown",
    ];

    public static bool IsPolicyId(string id) => PolicyIds.Contains(id, StringComparer.Ordinal);

    // One capital letter from A to Z, as the administrator's page sends it.
    public static bool IsDriveLetter(string key) => key is [>= 'A' and <= 'Z'];

    // Unknown ids in a stored value are dropped, so the answer only ever names policies the service knows.
    public static string[] ReadPolicies(string stored)
    {
        var ids = stored.Split(',', StringSplitOptions.RemoveEmptyEntries);
        return PolicyIds.Where(id => ids.Contains(id, StringComparer.Ordinal)).ToArray();
    }

    public static string[] ReadDrives(string stored) =>
        stored.Where(letter => letter is >= 'A' and <= 'Z').Distinct().Order().Select(letter => letter.ToString()).ToArray();

    // Only the listed policies change: true adds one, false removes it. The keys must already be checked with IsPolicyId.
    public static string ApplyPolicies(string stored, IReadOnlyDictionary<string, bool> changes)
    {
        var chosen = ReadPolicies(stored).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, isRestricted) in changes)
        {
            if (isRestricted)
            {
                chosen.Add(id);
            }
            else
            {
                chosen.Remove(id);
            }
        }

        return string.Join(',', PolicyIds.Where(chosen.Contains));
    }

    // Only the listed letters change: true adds one, false removes it. The keys must already be checked with IsDriveLetter.
    public static string ApplyDrives(string stored, IReadOnlyDictionary<string, bool> changes)
    {
        var chosen = ReadDrives(stored).ToHashSet(StringComparer.Ordinal);
        foreach (var (letter, isChosen) in changes)
        {
            if (isChosen)
            {
                chosen.Add(letter);
            }
            else
            {
                chosen.Remove(letter);
            }
        }

        return string.Concat(chosen.Order(StringComparer.Ordinal));
    }

    // The next value of a count that only has to differ from the last one: after int.MaxValue it wraps to 0, never negative.
    public static int NextCount(int count) => count == int.MaxValue ? 0 : count + 1;
}
