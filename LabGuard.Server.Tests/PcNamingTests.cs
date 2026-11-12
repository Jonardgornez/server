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

    [Fact]
    public void DisplayOrder_SortsByNumberValue()
    {
        string[] names = ["PC-10", "PC-2", "PC-11", "PC-1", "PC-3", "PC-9"];

        Assert.Equal(["PC-1", "PC-2", "PC-3", "PC-9", "PC-10", "PC-11"], names.Order(PcNaming.DisplayOrder));
    }

    [Fact]
    public void DisplayOrder_IgnoresCaseOfTheText()
    {
        string[] names = ["PC-3", "pc-2", "Pc-1"];

        Assert.Equal(["Pc-1", "pc-2", "PC-3"], names.Order(PcNaming.DisplayOrder));
    }

    [Fact]
    public void DisplayOrder_GroupsByTextBeforeNumber()
    {
        string[] names = ["PC-2", "Lab-10", "PC-1", "Lab-2"];

        Assert.Equal(["Lab-2", "Lab-10", "PC-1", "PC-2"], names.Order(PcNaming.DisplayOrder));
    }

    [Fact]
    public void DisplayOrder_PutsANameWithoutANumberBeforeNumberedOnes()
    {
        string[] names = ["PC-1", "PC-"];

        Assert.Equal(["PC-", "PC-1"], names.Order(PcNaming.DisplayOrder));
    }

    [Fact]
    public void DisplayOrder_ComparesLeadingZerosByValue()
    {
        string[] names = ["PC-10", "PC-02", "PC-1"];

        Assert.Equal(["PC-1", "PC-02", "PC-10"], names.Order(PcNaming.DisplayOrder));
    }

    [Fact]
    public void DisplayOrder_HandlesNumbersTooLongForAnInteger()
    {
        string[] names = ["PC-123456789012345678901234567890", "PC-99999999999999999999"];

        Assert.Equal(["PC-99999999999999999999", "PC-123456789012345678901234567890"], names.Order(PcNaming.DisplayOrder));
    }

    [Fact]
    public void DisplayOrder_SameValueDifferentSpelling_IsStillOrdered()
    {
        Assert.NotEqual(0, PcNaming.DisplayOrder.Compare("PC-1", "PC-01"));
        Assert.Equal(0, PcNaming.DisplayOrder.Compare("PC-1", "PC-1"));
    }

    [Theory]
    [InlineData(1, "PC-1")]
    [InlineData(9, "PC-9")]
    [InlineData(10, "PC-10")]
    [InlineData(999_999_999, "PC-999999999")]
    public void NameFor_ReturnsThePrefixAndTheNumber(int number, string expected)
    {
        Assert.Equal(expected, PcNaming.NameFor(number));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(1_000_000_000)]
    [InlineData(int.MaxValue)]
    public void NameFor_OutOfRange_Throws(int number)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PcNaming.NameFor(number));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(999_999_999, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(1_000_000_000, false)]
    public void IsValidNumber_AcceptsOnlyOneToNineDigits(int number, bool expected)
    {
        Assert.Equal(expected, PcNaming.IsValidNumber(number));
    }
}
