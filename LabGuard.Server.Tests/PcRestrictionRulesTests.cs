using LabGuard.Server.Pcs;

namespace LabGuard.Server.Tests;

// How a PC row stores the Restrictions policies and drives, and how a change touches only the keys it lists.
public class PcRestrictionRulesTests
{
    [Theory]
    [InlineData("registry-editor", true)]
    [InlineData("windows-shutdown", true)]
    [InlineData("task-manager", false)]
    [InlineData("fast-user-switch", false)]
    [InlineData("file-folder-protection", false)]
    [InlineData("Registry-Editor", false)]
    [InlineData("", false)]
    public void IsPolicyId_KnowsOnlyThisRoutesPolicies(string id, bool expected)
    {
        Assert.Equal(expected, PcRestrictionRules.IsPolicyId(id));
    }

    [Theory]
    [InlineData("A", true)]
    [InlineData("Z", true)]
    [InlineData("c", false)]
    [InlineData("CD", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData("Ä", false)]
    public void IsDriveLetter_TakesOneCapitalLetter(string key, bool expected)
    {
        Assert.Equal(expected, PcRestrictionRules.IsDriveLetter(key));
    }

    [Fact]
    public void ReadPolicies_KeepsTheTableOrder_AndDropsUnknownIds()
    {
        Assert.Equal(["registry-editor", "control-panel"], PcRestrictionRules.ReadPolicies("control-panel,old-id,registry-editor"));
        Assert.Empty(PcRestrictionRules.ReadPolicies(""));
    }

    [Fact]
    public void ReadDrives_ListsLettersInOrder()
    {
        Assert.Equal(["C", "D"], PcRestrictionRules.ReadDrives("DC"));
        Assert.Empty(PcRestrictionRules.ReadDrives(""));
    }

    [Fact]
    public void ApplyPolicies_ChangesOnlyTheListedIds()
    {
        var stored = PcRestrictionRules.ApplyPolicies(
            "run-command,control-panel",
            new Dictionary<string, bool> { ["control-panel"] = false, ["registry-editor"] = true });

        Assert.Equal("registry-editor,run-command", stored);
    }

    [Fact]
    public void ApplyDrives_ChangesOnlyTheListedLetters()
    {
        var stored = PcRestrictionRules.ApplyDrives("CE", new Dictionary<string, bool> { ["E"] = false, ["A"] = true });

        Assert.Equal("AC", stored);
    }

    [Fact]
    public void ApplyWithNoChanges_KeepsTheStoredValue()
    {
        Assert.Equal("command-prompt", PcRestrictionRules.ApplyPolicies("command-prompt", new Dictionary<string, bool>()));
        Assert.Equal("D", PcRestrictionRules.ApplyDrives("D", new Dictionary<string, bool>()));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(41, 42)]
    [InlineData(int.MaxValue, 0)]
    public void NextCount_NeverGoesNegative(int count, int expected)
    {
        Assert.Equal(expected, PcRestrictionRules.NextCount(count));
    }
}
