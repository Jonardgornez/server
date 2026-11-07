using LabGuard.Server.Pcs;

namespace LabGuard.Server.Tests;

public class PcMessageRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    private static (bool Accepted, string Text, string Error) Check(string? raw)
    {
        var accepted = PcMessageRules.TryCheckText(raw, out var text, out var error);
        return (accepted, text, error);
    }

    [Theory]
    [InlineData("Please save your work.", "Please save your work.")]
    [InlineData("  padded  ", "padded")]
    [InlineData("first\nsecond", "first\nsecond")]
    [InlineData("first\r\nsecond", "first\nsecond")]
    [InlineData("first\rsecond", "first\nsecond")]
    [InlineData("\r\nhello\r\n", "hello")]
    [InlineData("Ünïcödé and 😀 emoji", "Ünïcödé and 😀 emoji")]
    public void TryCheckText_AcceptsPlainText_AndStoresItNormalised(string raw, string expected)
    {
        var result = Check(raw);

        Assert.True(result.Accepted);
        Assert.Equal(expected, result.Text);
        Assert.Equal("", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n  \r\n")]
    [InlineData("\t \t")]
    public void TryCheckText_EmptyOrWhitespace_IsRefused(string? raw)
    {
        var result = Check(raw);

        Assert.False(result.Accepted);
        Assert.Equal("Enter a message.", result.Error);
    }

    [Fact]
    public void TryCheckText_500Characters_IsAccepted()
    {
        var text = new string('a', 500);

        Assert.True(Check(text).Accepted);
    }

    [Fact]
    public void TryCheckText_501Characters_IsRefused()
    {
        var result = Check(new string('a', 501));

        Assert.False(result.Accepted);
        Assert.Equal("A message can be at most 500 characters.", result.Error);
    }

    [Fact]
    public void TryCheckText_LengthIsCountedAfterTrimming()
    {
        var padded = "   " + new string('a', 500) + "   ";

        var result = Check(padded);

        Assert.True(result.Accepted);
        Assert.Equal(500, result.Text.Length);
    }

    [Fact]
    public void TryCheckText_CrLfCountsAsOneCharacter()
    {
        // Ten lines of 49 characters joined by CRLF: 490 + 9 line breaks is 499 once each break is one line feed.
        // Counting each CRLF as two characters would give 508 and refuse it.
        var text = string.Join("\r\n", Enumerable.Repeat(new string('a', 49), 10));

        var result = Check(text);

        Assert.True(result.Accepted);
        Assert.Equal(499, result.Text.Length);
    }

    [Fact]
    public void TryCheckText_TenLines_IsAccepted()
    {
        var text = string.Join("\n", Enumerable.Repeat("line", 10));

        Assert.True(Check(text).Accepted);
    }

    [Fact]
    public void TryCheckText_ElevenLines_IsRefused()
    {
        var text = string.Join("\r\n", Enumerable.Repeat("line", 11));

        var result = Check(text);

        Assert.False(result.Accepted);
        Assert.Equal("A message can be at most 10 lines.", result.Error);
    }

    [Theory]
    [InlineData("\u0000")]
    [InlineData("tab\there")]
    [InlineData("bell\u0007")]
    [InlineData("escape\u001B")]
    [InlineData("\u007F")]
    [InlineData("next\u0085line")]
    [InlineData("\u009F")]
    public void TryCheckText_ControlCharacter_IsRefused(string raw)
    {
        var result = Check(raw);

        Assert.False(result.Accepted);
        Assert.Equal("A message can't contain control characters.", result.Error);
    }

    // Built from char codes at run time: test data is serialized for discovery, which would turn a lone surrogate into U+FFFD.
    [Fact]
    public void TryCheckText_LoneSurrogate_IsRefused()
    {
        var lowInTheMiddle = "a" + (char)0xD800 + "b";
        var lowAtTheStart = (char)0xDC00 + " after";
        var highAtTheEnd = "ends with high " + (char)0xD83D;

        foreach (var raw in new[] { lowInTheMiddle, lowAtTheStart, highAtTheEnd })
        {
            var result = Check(raw);

            Assert.False(result.Accepted);
            Assert.Equal("A message contains a character that can't be shown.", result.Error);
        }
    }

    [Fact]
    public void TryCheckText_SurrogatePair_IsAccepted()
    {
        Assert.True(Check("😀").Accepted);
    }

    [Fact]
    public void RecipientsError_OneToHundredDistinctIds_IsNull()
    {
        Assert.Null(PcMessageRules.RecipientsError([1]));
        Assert.Null(PcMessageRules.RecipientsError(Enumerable.Range(1, 100).ToArray()));
    }

    [Fact]
    public void RecipientsError_NoIds_IsRefused()
    {
        Assert.Equal("Choose at least one PC.", PcMessageRules.RecipientsError([]));
    }

    [Fact]
    public void RecipientsError_MoreThanHundredIds_IsRefused()
    {
        Assert.Equal("Choose at most 100 PCs.", PcMessageRules.RecipientsError(Enumerable.Range(1, 101).ToArray()));
    }

    [Fact]
    public void RecipientsError_DuplicateId_IsRefused()
    {
        Assert.Equal("Each PC can be chosen only once.", PcMessageRules.RecipientsError([4, 9, 4]));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(5.999, true)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    public void IsInDeliveryWindow_OnlyBeforeSixSeconds(double secondsOld, bool expected)
    {
        var created = Now.AddSeconds(-secondsOld);

        Assert.Equal(expected, PcMessageRules.IsInDeliveryWindow(created, Now));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void IsInAckWindow_UpToSixtySeconds(double secondsOld, bool expected)
    {
        var created = Now.AddSeconds(-secondsOld);

        Assert.Equal(expected, PcMessageRules.IsInAckWindow(created, Now));
    }

    [Fact]
    public void IsInAckWindow_CreatedInTheFuture_IsRecent()
    {
        // A clock moved back must not refuse an acknowledgement.
        Assert.True(PcMessageRules.IsInAckWindow(Now.AddSeconds(30), Now));
    }
}
