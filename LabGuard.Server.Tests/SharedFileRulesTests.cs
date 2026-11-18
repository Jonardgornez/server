using LabGuard.Server.SharedFiles;

namespace LabGuard.Server.Tests;

public class SharedFileRulesTests
{
    // Names

    [Theory]
    [InlineData("notes.pdf")]
    [InlineData("a")]
    [InlineData("Report 2026 (final).docx")]
    [InlineData("ünïcödé 文件.txt")]
    [InlineData(".gitignore")]
    [InlineData("CONSOLE.txt")]
    [InlineData("COM10")]
    [InlineData("my con.txt")]
    [InlineData("CONIN.txt")]
    [InlineData("COM10.txt")]
    public void IsValidSegment_AcceptsOrdinaryNames(string name) =>
        Assert.True(SharedFileRules.IsValidSegment(name, 200));

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a:b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a\0b")]
    [InlineData("a\tb")]
    [InlineData("a\u001fb")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData(" name")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN.txt")]
    [InlineData("aux.tar.gz")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("com9.log")]
    [InlineData("LPT1")]
    [InlineData("lpt9.txt")]
    [InlineData("CON .txt")]
    [InlineData("COM¹")]
    [InlineData("COM0")]
    [InlineData("com0.txt")]
    [InlineData("LPT0")]
    [InlineData("lpt0 .log")]
    [InlineData("com².txt")]
    [InlineData("COM³")]
    [InlineData("LPT¹.log")]
    [InlineData("lpt²")]
    [InlineData("LPT³ .txt")]
    [InlineData("CONIN$")]
    [InlineData("conin$.txt")]
    [InlineData("CONOUT$")]
    [InlineData("ConOut$ .log")]
    public void IsValidSegment_RefusesWhatWindowsCantStoreSafely(string name) =>
        Assert.False(SharedFileRules.IsValidSegment(name, 200));

    // Invisible format characters (direction marks, zero-width space, BOM, soft hyphen) and DEL.
    [Theory]
    [InlineData(0x200E)]
    [InlineData(0x202E)]
    [InlineData(0x200B)]
    [InlineData(0xFEFF)]
    [InlineData(0x00AD)]
    [InlineData(0x2066)]
    [InlineData(0x007F)]
    [InlineData(0x0080)]
    [InlineData(0x0085)]
    [InlineData(0x009F)]
    public void IsValidSegment_RefusesFormatCharactersAndDelete(int code) =>
        Assert.False(SharedFileRules.IsValidSegment("ab" + (char)code + "c.txt", 200));

    // Format characters outside the BMP, such as the language tags, are checked per code point.
    [Theory]
    [InlineData(0xE0001)]
    [InlineData(0xE0041)]
    [InlineData(0x1D173)]
    public void IsValidSegment_RefusesNonBmpFormatCharacters(int codePoint) =>
        Assert.False(SharedFileRules.IsValidSegment("ab" + char.ConvertFromUtf32(codePoint) + "c.txt", 200));

    [Fact]
    public void IsValidSegment_AcceptsNonBmpCharactersThatAreNotFormat() =>
        Assert.True(SharedFileRules.IsValidSegment("photo " + char.ConvertFromUtf32(0x1F600) + ".jpg", 200));

    [Theory]
    [InlineData("desktop.ini")]
    [InlineData("DESKTOP.INI")]
    [InlineData("Desktop.Ini")]
    public void IsValidSegment_RefusesDesktopIni(string name) =>
        Assert.False(SharedFileRules.IsValidSegment(name, 200));

    [Theory]
    [InlineData("REPORT~1.PDF")]
    [InlineData("LYKANT~1")]
    [InlineData("AB~12.TXT")]
    [InlineData("report~1.pdf")]
    [InlineData("A~123456")]
    [InlineData("notes~12.txt")]
    [InlineData("X~1.A")]
    public void IsValidSegment_RefusesShortNames(string name) =>
        Assert.False(SharedFileRules.IsValidSegment(name, 200));

    [Theory]
    [InlineData("desktop.ini.txt")]
    [InlineData("my desktop.ini")]
    [InlineData("backup~2024.zip")]
    [InlineData("notes~draft.txt")]
    [InlineData("my~file.txt")]
    [InlineData("verylong~1.txt")]
    [InlineData("a~b.txt")]
    [InlineData("a~1b.txt")]
    [InlineData("~report.docx")]
    [InlineData("a~1.b.c")]
    [InlineData("AB~1.HTML")]
    [InlineData("AB~1234567")]
    [InlineData("AB~١")]
    public void IsValidSegment_AcceptsNamesThatOnlyLookSimilar(string name) =>
        Assert.True(SharedFileRules.IsValidSegment(name, 200));

    [Theory]
    [InlineData("REPORT~1.PDF", true)]
    [InlineData("ABCDEF~1", true)]
    [InlineData("ABCDEF~12", false)]
    [InlineData("ABCDEF~123", false)]
    [InlineData("ABCDE~123.TXT", false)]
    [InlineData("ABCD~123.TXT", true)]
    [InlineData("backup~2024.zip", false)]
    public void IsShortName_KeepsTheBaseWithinEightCharacters(string name, bool expected) =>
        Assert.Equal(expected, SharedFileRules.IsShortName(name));

    [Theory]
    [InlineData("desktop.ini")]
    [InlineData("a/PROGRA~1")]
    [InlineData("a\u200Eb")]
    public void TryNormalizeSubfolder_AppliesTheSameNameRules(string raw) =>
        Assert.False(SharedFileRules.TryNormalizeSubfolder(raw, out _));

    [Theory]
    [InlineData("folder/desktop.ini")]
    [InlineData("REPORT~1.PDF")]
    public void TryCleanFileName_AppliesTheSameNameRules(string raw) =>
        Assert.False(SharedFileRules.TryCleanFileName(raw, out _));

    [Fact]
    public void TryCleanFileName_RefusesAFormatCharacter() =>
        Assert.False(SharedFileRules.TryCleanFileName("invoice" + (char)0x202E + "fdp.exe", out _));

    [Fact]
    public void CleanError_RemovesFormatCharacters() =>
        Assert.Equal("Disk full", SharedFileRules.CleanError("Disk" + (char)0x200B + " full" + (char)0x202E));

    [Fact]
    public void CleanError_RemovesNonBmpFormatAndC1Characters() =>
        Assert.Equal("Disk full", SharedFileRules.CleanError("Disk" + char.ConvertFromUtf32(0xE0001) + " full" + (char)0x0085));

    [Fact]
    public void IsValidSegment_RefusesAnUnpairedSurrogate() =>
        Assert.False(SharedFileRules.IsValidSegment("a" + (char)0xD800 + "b", 200));

    [Fact]
    public void IsValidSegment_EnforcesTheLengthLimit()
    {
        Assert.True(SharedFileRules.IsValidSegment(new string('a', 64), 64));
        Assert.False(SharedFileRules.IsValidSegment(new string('a', 65), 64));
    }

    [Theory]
    [InlineData("notes.pdf", "notes.pdf")]
    [InlineData("C:\\Users\\me\\notes.pdf", "notes.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("folder/sub\\notes.pdf", "notes.pdf")]
    public void TryCleanFileName_KeepsOnlyTheLastPathPart(string raw, string expected)
    {
        Assert.True(SharedFileRules.TryCleanFileName(raw, out var name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("folder/")]
    [InlineData("..")]
    [InlineData("a/..")]
    [InlineData("CON")]
    [InlineData("bad|name.txt")]
    [InlineData("trailing.")]
    public void TryCleanFileName_RefusesAnInvalidName(string? raw)
    {
        Assert.False(SharedFileRules.TryCleanFileName(raw, out var name));
        Assert.Equal("", name);
    }

    [Fact]
    public void TryCleanFileName_AllowsUpTo200Characters()
    {
        Assert.True(SharedFileRules.TryCleanFileName(new string('a', 196) + ".txt", out _));
        Assert.False(SharedFileRules.TryCleanFileName(new string('a', 197) + ".txt", out _));
    }

    // Subfolders

    [Fact]
    public void TryNormalizeSubfolder_Null_IsTheDefault()
    {
        Assert.True(SharedFileRules.TryNormalizeSubfolder(null, out var subfolder));
        Assert.Equal("Lykantra Share", subfolder);
    }

    [Fact]
    public void TryNormalizeSubfolder_Empty_SavesStraightIntoTheFolder()
    {
        Assert.True(SharedFileRules.TryNormalizeSubfolder("", out var subfolder));
        Assert.Equal("", subfolder);
    }

    [Theory]
    [InlineData("Class 7", "Class 7")]
    [InlineData("a/b", "a\\b")]
    [InlineData("a\\b\\c", "a\\b\\c")]
    [InlineData("a/b\\c", "a\\b\\c")]
    public void TryNormalizeSubfolder_JoinsUpToThreeNamesWithBackslashes(string raw, string expected)
    {
        Assert.True(SharedFileRules.TryNormalizeSubfolder(raw, out var subfolder));
        Assert.Equal(expected, subfolder);
    }

    [Theory]
    [InlineData("a/b/c/d")]
    [InlineData("\\a")]
    [InlineData("/a")]
    [InlineData("C:\\a")]
    [InlineData("C:")]
    [InlineData("\\\\server\\share")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("./a")]
    [InlineData("a/CON")]
    [InlineData("lpt1.d")]
    [InlineData(" a")]
    [InlineData("a ")]
    [InlineData("a.")]
    [InlineData("a*")]
    [InlineData("a\nb")]
    [InlineData(" ")]
    public void TryNormalizeSubfolder_RefusesUnsafeOrRootedFolders(string raw) =>
        Assert.False(SharedFileRules.TryNormalizeSubfolder(raw, out _));

    [Fact]
    public void TryNormalizeSubfolder_AllowsNamesUpTo64Characters()
    {
        Assert.True(SharedFileRules.TryNormalizeSubfolder(new string('a', 64), out _));
        Assert.False(SharedFileRules.TryNormalizeSubfolder(new string('a', 65), out _));
    }

    // Folders, decisions and reports

    [Theory]
    [InlineData("desktop", true)]
    [InlineData("documents", true)]
    [InlineData("downloads", true)]
    [InlineData("Desktop", false)]
    [InlineData("pictures", false)]
    [InlineData("", false)]
    public void IsKnownFolder_AcceptsOnlyTheThreeFolders(string folder, bool expected) =>
        Assert.Equal(expected, SharedFileRules.IsKnownFolder(folder));

    [Theory]
    [InlineData("overwrite", true)]
    [InlineData("keepBoth", true)]
    [InlineData("skip", true)]
    [InlineData("keepboth", false)]
    [InlineData("delete", false)]
    public void IsDecision_AcceptsOnlyTheThreeDecisions(string decision, bool expected) =>
        Assert.Equal(expected, SharedFileRules.IsDecision(decision));

    [Theory]
    [InlineData(null, "The PC couldn't save the file.")]
    [InlineData("   ", "The PC couldn't save the file.")]
    [InlineData(" Not enough free space ", "Not enough free space")]
    [InlineData("Folder\r\nnot allowed\0", "Foldernot allowed")]
    public void CleanError_RemovesControlCharactersAndTrims(string? raw, string expected) =>
        Assert.Equal(expected, SharedFileRules.CleanError(raw));

    [Fact]
    public void CleanError_CutsToTheLimit() =>
        Assert.Equal(200, SharedFileRules.CleanError(new string('x', 500)).Length);

    [Fact]
    public void EffectiveStatus_WaitingPastItsLifetime_IsExpired()
    {
        var created = DateTimeOffset.UnixEpoch;

        Assert.Equal("waiting", SharedFileRules.EffectiveStatus("waiting", created, created + TimeSpan.FromMinutes(59)));
        Assert.Equal("expired", SharedFileRules.EffectiveStatus("waiting", created, created + TimeSpan.FromHours(1)));
        Assert.Equal("saved", SharedFileRules.EffectiveStatus("saved", created, created + TimeSpan.FromHours(5)));
    }

    [Theory]
    [InlineData("saved", new[] { "downloading", "applying" })]
    [InlineData("failed", new[] { "downloading", "applying" })]
    [InlineData("exists", new[] { "downloading" })]
    [InlineData("skipped", new[] { "applying" })]
    [InlineData("waiting", new string[0])]
    [InlineData("downloading", new string[0])]
    [InlineData("expired", new string[0])]
    [InlineData("SAVED", new string[0])]
    public void AllowedFrom_ListsTheStatusesAReportMayLeave(string reported, string[] expected) =>
        Assert.Equal(expected, SharedFileRules.AllowedFrom(reported));
}
