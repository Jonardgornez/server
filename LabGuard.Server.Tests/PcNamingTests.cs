using LabGuard.Server.Clients;

namespace LabGuard.Server.Tests;

public class PcNamingTests
{
    [Fact]
    public void NextFreeName_WithNoNames_ReturnsPc1()
    {
        Assert.Equal("PC-1", PcNaming.NextFreeName([]));
    }

    [Fact]
    public void NextFreeName_AfterConsecutiveNames_ReturnsTheNextNumber()
    {
        Assert.Equal("PC-3", PcNaming.NextFreeName(["PC-1", "PC-2"]));
    }

    [Fact]
    public void NextFreeName_WithAGap_ReturnsTheLowestFreeNumber()
    {
        Assert.Equal("PC-2", PcNaming.NextFreeName(["PC-1", "PC-3"]));
    }

    [Fact]
    public void NextFreeName_IgnoresTheOrderOfTheUsedNames()
    {
        Assert.Equal("PC-4", PcNaming.NextFreeName(["PC-3", "PC-1", "PC-2"]));
    }

    [Theory]
    [InlineData("PC")]
    [InlineData("PC-")]
    [InlineData("test")]
    [InlineData("Lab-1")]
    [InlineData("PC-01")]
    [InlineData("PC-0")]
    [InlineData("PC-1a")]
    [InlineData("PC-1 ")]
    [InlineData("PC--1")]
    [InlineData("PC-1000000000")]
    public void NextFreeName_IgnoresNamesThatAreNotNumbered(string name)
    {
        Assert.Equal("PC-1", PcNaming.NextFreeName([name]));
    }

    [Theory]
    [InlineData("pc-1")]
    [InlineData("Pc-1")]
    public void NextFreeName_IgnoresCase_WhenMatchingUsedNames(string name)
    {
        Assert.Equal("PC-2", PcNaming.NextFreeName([name]));
    }
}
