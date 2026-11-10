using LabGuard.Server.Pcs;

namespace LabGuard.Server.Tests;

public class PcLockMessageRulesTests
{
    private static (bool Accepted, string? Message, string Error) Check(string? raw)
    {
        var accepted = PcLockMessageRules.TryCheck(raw, out var message, out var error);
        return (accepted, message, error);
    }

    [Theory]
    [InlineData("Exam in progress.", "Exam in progress.")]
    [InlineData("  Padded  ", "Padded")]
    [InlineData("Ünïcödé and 😀 emoji", "Ünïcödé and 😀 emoji")]
    public void TryCheck_AcceptsText_AndStoresItTrimmed(string raw, string expected)
    {
        var result = Check(raw);

        Assert.True(result.Accepted);
        Assert.Equal(expected, result.Message);
        Assert.Equal("", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCheck_NoMessage_IsAcceptedAsNull(string? raw)
    {
        var result = Check(raw);

        Assert.True(result.Accepted);
        Assert.Null(result.Message);
    }

    [Fact]
    public void TryCheck_200Characters_IsAccepted()
    {
        var result = Check(new string('m', 200));

        Assert.True(result.Accepted);
        Assert.Equal(200, result.Message!.Length);
    }

    [Fact]
    public void TryCheck_200CharactersAfterTrimming_IsAccepted()
    {
        Assert.True(Check("  " + new string('m', 200) + "  ").Accepted);
    }

    [Fact]
    public void TryCheck_201Characters_IsRefusedWithTheLimit()
    {
        var result = Check(new string('m', 201));

        Assert.False(result.Accepted);
        Assert.Null(result.Message);
        Assert.Equal("The message must be 200 characters or fewer.", result.Error);
    }

    [Theory]
    [InlineData("first\nsecond")]
    [InlineData("first\r\nsecond")]
    [InlineData("first\rsecond")]
    [InlineData("tab\there")]
    [InlineData("bell\u0007here")]
    public void TryCheck_LineBreaksOrControlCharacters_AreRefused(string raw)
    {
        var result = Check(raw);

        Assert.False(result.Accepted);
        Assert.Equal("The message can't contain line breaks or control characters.", result.Error);
    }

    // Built from char codes at run time: test data is serialized for discovery, which would turn a lone surrogate into U+FFFD.
    [Fact]
    public void TryCheck_LoneSurrogate_IsRefused()
    {
        var result = Check("a" + (char)0xD800 + "b");

        Assert.False(result.Accepted);
        Assert.Equal("The message can't contain a character that can't be shown.", result.Error);
    }

    [Fact]
    public void TryCheck_SurrogatePair_IsAccepted()
    {
        Assert.True(Check("😀").Accepted);
    }
}
