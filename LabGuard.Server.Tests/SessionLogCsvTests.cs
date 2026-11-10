using System.Text;
using LabGuard.Server.Data;
using LabGuard.Server.Logs;
using LabGuard.Server.Sessions;

namespace LabGuard.Server.Tests;

public class SessionLogCsvTests
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static readonly TimeZoneInfo PlusTen = TimeZoneInfo.CreateCustomTimeZone("UTC+10", TimeSpan.FromHours(10), "UTC+10", "UTC+10");
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes, Bom.Length, bytes.Length - Bom.Length);

    [Fact]
    public void Write_StartsWithAUtf8ByteOrderMark()
    {
        var bytes = SessionLogCsv.Write([], Now, PlusTen);

        Assert.Equal(Bom, bytes.Take(Bom.Length).ToArray());
    }

    [Fact]
    public void Write_NoSessions_IsTheHeaderRowOnly()
    {
        var text = Text(SessionLogCsv.Write([], Now, PlusTen));

        Assert.Equal("PC,User or source,Start,End,Duration (minutes),Ended by\r\n", text);
    }

    [Fact]
    public void Write_Rows_ShowLocalTimesDurationsAndLabels()
    {
        var sessions = new[]
        {
            new PcSession
            {
                PcName = "PC-01",
                Username = "alice",
                StartedAtUtc = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero),
                EndedAtUtc = new DateTimeOffset(2026, 11, 9, 1, 30, 0, TimeSpan.Zero),
                EndReason = SessionLifecycle.Logout,
            },
            new PcSession
            {
                PcName = "PC-02",
                Username = "Admin unlock",
                StartedAtUtc = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero),
                IsAdminUnlock = true,
            },
        };

        var text = Text(SessionLogCsv.Write(sessions, Now, PlusTen));

        Assert.Equal(
            "PC,User or source,Start,End,Duration (minutes),Ended by\r\n"
            + "PC-01,alice,2026-11-09 10:00,2026-11-09 11:30,90,Logout\r\n"
            + "PC-02,Admin unlock,2026-11-09 20:00,,120,Running\r\n",
            text);
    }

    [Fact]
    public void Write_UsesCrlfLineEndings()
    {
        var text = Text(SessionLogCsv.Write([new PcSession { PcName = "PC-01", Username = "a", StartedAtUtc = Now }], Now, PlusTen));

        Assert.Equal(2, text.Split("\r\n").Length - 1);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    [Fact]
    public void Write_UserControlledNamesWithCommasAndQuotes_AreQuoted()
    {
        var session = new PcSession { PcName = "Room, \"A\"", Username = "line\nbreak", StartedAtUtc = Now };

        var text = Text(SessionLogCsv.Write([session], Now, PlusTen));

        Assert.Contains("\"Room, \"\"A\"\"\",\"line\nbreak\",", text);
    }

    [Fact]
    public void Write_FormulaLikeName_IsNeutralised()
    {
        var session = new PcSession { PcName = "=HYPERLINK(\"http://x\")", Username = "+1", StartedAtUtc = Now };

        var text = Text(SessionLogCsv.Write([session], Now, PlusTen));

        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\")\",'+1,", text);
    }

    [Fact]
    public void Write_TimesAreFormattedInTheGivenZone()
    {
        var session = new PcSession { PcName = "PC-01", Username = "a", StartedAtUtc = new DateTimeOffset(2026, 11, 8, 23, 59, 0, TimeSpan.Zero) };

        var text = Text(SessionLogCsv.Write([session], Now, PlusTen));

        Assert.Contains(",2026-11-09 09:59,", text);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("carriage\rreturn", "\"carriage\rreturn\"")]
    [InlineData("Lab=1", "Lab=1")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@user", "'@user")]
    [InlineData("\ttabbed", "'\ttabbed")]
    [InlineData("\rfirst", "\"'\rfirst\"")]
    [InlineData("=1,2", "\"'=1,2\"")]
    public void Field_QuotesAndNeutralisesAsRequired(string value, string expected)
    {
        Assert.Equal(expected, SessionLogCsv.Field(value));
    }

    [Fact]
    public void FileNameFor_UsesTheLocalTimeOfTheExport()
    {
        // 14:05 UTC is 00:05 on the next day in UTC+10.
        var name = SessionLogCsv.FileNameFor(new DateTimeOffset(2026, 11, 9, 14, 5, 0, TimeSpan.Zero), PlusTen);

        Assert.Equal("session-logs-20261110-0005.csv", name);
    }
}
