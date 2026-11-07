using LabGuard.Server.Common;

namespace LabGuard.Server.Tests;

public class UnpairedSurrogatesTests
{
    // Built from char codes at run time: test data is serialized for discovery, which would turn a lone surrogate into U+FFFD.
    [Fact]
    public void IsPresentIn_LoneSurrogate_IsTrueWhereverItSits()
    {
        Assert.True(UnpairedSurrogates.IsPresentIn("a" + (char)0xD800 + "b"));
        Assert.True(UnpairedSurrogates.IsPresentIn((char)0xDC00 + "start"));
        Assert.True(UnpairedSurrogates.IsPresentIn("end " + (char)0xD83D));
    }

    [Fact]
    public void IsPresentIn_SurrogatePairAndPlainText_IsFalse()
    {
        Assert.False(UnpairedSurrogates.IsPresentIn("😀 smile"));
        Assert.False(UnpairedSurrogates.IsPresentIn("plain text"));
        Assert.False(UnpairedSurrogates.IsPresentIn(""));
    }
}
