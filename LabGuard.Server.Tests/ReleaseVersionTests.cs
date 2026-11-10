using LabGuard.Server.ClientUpdates;

namespace LabGuard.Server.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.20.0", 0, 20, 0)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("0.0.0", 0, 0, 0)]
    [InlineData("10.20.3", 10, 20, 3)]
    [InlineData("0.999999999.0", 0, 999999999, 0)]
    public void TryParse_ReadsThreeNumbers(string text, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));

        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.20")]
    [InlineData("0.20.0.0")]
    [InlineData("0.20.x")]
    [InlineData("v0.20.0")]
    [InlineData("0.-1.0")]
    [InlineData("0.+1.0")]
    [InlineData(" 0.20.0")]
    [InlineData("0.20.0 ")]
    [InlineData("0..0")]
    [InlineData("0.2147483648.0")]
    [InlineData("99999999999999999999.0.0")]
    [InlineData("0.1000000000.0")]
    [InlineData("0.2147483647.0")]
    [InlineData("0.00.0")]
    [InlineData("0.020.0")]
    [InlineData("00.1.0")]
    [InlineData("0.09.0")]
    [InlineData("0.-0.0")]
    [InlineData("0.20.0\n")]
    public void TryParse_RefusesAnythingElse(string text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }

    [Fact]
    public void TryParse_AcceptsNineDigits_TheLargestPartTheClientAccepts()
    {
        Assert.True(ReleaseVersion.TryParse("0.999999999.0", out var version));

        Assert.Equal(999999999, version.Minor);
    }

    [Fact]
    public void TryParse_RefusesNull()
    {
        Assert.False(ReleaseVersion.TryParse(null, out _));
    }

    [Fact]
    public void TryParseFileVersion_ReadsTheFirstThreeParts_OfAFourPartResource()
    {
        Assert.True(ReleaseVersion.TryParseFileVersion("0.19.0.7", out var version));

        Assert.Equal(new ReleaseVersion(0, 19, 0), version);
    }

    [Theory]
    [InlineData("0.19")]
    [InlineData("0.019.0.0")]
    [InlineData("a.b.c")]
    [InlineData(null)]
    public void TryParseFileVersion_RefusesAResourceWithoutThreeNumbers(string? text)
    {
        Assert.False(ReleaseVersion.TryParseFileVersion(text, out _));
    }

    [Fact]
    public void Compare_IsNumeric_NotTextual()
    {
        Assert.True(new ReleaseVersion(0, 10, 0) > new ReleaseVersion(0, 9, 0));
        Assert.True(new ReleaseVersion(1, 0, 0) > new ReleaseVersion(0, 99, 99));
        Assert.True(new ReleaseVersion(0, 20, 0) <= new ReleaseVersion(0, 20, 0));
        Assert.True(new ReleaseVersion(0, 20, 0) >= new ReleaseVersion(0, 20, 0));
        Assert.False(new ReleaseVersion(0, 20, 0) < new ReleaseVersion(0, 20, 0));
    }

    [Fact]
    public void ToString_WritesMajorMinorPatch()
    {
        Assert.Equal("0.20.0", new ReleaseVersion(0, 20, 0).ToString());
    }

    [Fact]
    public void Parse_OfAStoredVersion_ReturnsIt()
    {
        Assert.Equal(new ReleaseVersion(0, 19, 0), ReleaseVersion.Parse("0.19.0"));
    }

    [Fact]
    public void Parse_OfAnUnreadableStoredVersion_Throws()
    {
        Assert.Throws<FormatException>(() => ReleaseVersion.Parse("0.19"));
    }
}
