using System.Text;
using LabGuard.Server.Backgrounds;

namespace LabGuard.Server.Tests;

public class BackgroundImageRulesTests
{
    private static readonly byte[] GarbageBytes = "this is not an image"u8.ToArray();

    [Fact]
    public void IsValidImage_PngSignatureWithPngType_IsValid()
    {
        Assert.True(BackgroundImageRules.IsValidImage("image/png", BackgroundTestImages.Png()));
    }

    [Fact]
    public void IsValidImage_JpegSignatureWithJpegType_IsValid()
    {
        Assert.True(BackgroundImageRules.IsValidImage("image/jpeg", BackgroundTestImages.Jpeg()));
    }

    [Fact]
    public void IsValidImage_JpegBytesDeclaredAsPng_IsNotValid()
    {
        Assert.False(BackgroundImageRules.IsValidImage("image/png", BackgroundTestImages.Jpeg()));
    }

    [Fact]
    public void IsValidImage_PngBytesDeclaredAsJpeg_IsNotValid()
    {
        Assert.False(BackgroundImageRules.IsValidImage("image/jpeg", BackgroundTestImages.Png()));
    }

    [Fact]
    public void IsValidImage_GarbageBytes_IsNotValid()
    {
        Assert.False(BackgroundImageRules.IsValidImage("image/png", GarbageBytes));
        Assert.False(BackgroundImageRules.IsValidImage("image/jpeg", GarbageBytes));
    }

    [Fact]
    public void IsValidImage_EmptyBytes_IsNotValid()
    {
        Assert.False(BackgroundImageRules.IsValidImage("image/png", []));
        Assert.False(BackgroundImageRules.IsValidImage("image/jpeg", []));
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("")]
    [InlineData("application/octet-stream")]
    public void IsValidImage_UnsupportedType_IsNotValid(string mediaType)
    {
        Assert.False(BackgroundImageRules.IsValidImage(mediaType, BackgroundTestImages.Png()));
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("image/gif", false)]
    [InlineData("", false)]
    public void IsSupportedMediaType_OnlyPngAndJpeg(string mediaType, bool expected)
    {
        Assert.Equal(expected, BackgroundImageRules.IsSupportedMediaType(mediaType));
    }

    [Theory]
    [InlineData("image/png", "image/png")]
    [InlineData("image/png; charset=binary", "image/png")]
    [InlineData("  IMAGE/JPEG ", "image/jpeg")]
    [InlineData(null, "")]
    public void MediaTypeOf_DropsParametersAndLowercases(string? header, string expected)
    {
        Assert.Equal(expected, BackgroundImageRules.MediaTypeOf(header));
    }

    [Fact]
    public void IsWithinSizeLimit_ExactlyFiveMegabytes_IsWithin()
    {
        Assert.True(BackgroundImageRules.IsWithinSizeLimit(BackgroundTestImages.FiveMegabytes));
    }

    [Fact]
    public void IsWithinSizeLimit_OneByteOverFiveMegabytes_IsNotWithin()
    {
        Assert.False(BackgroundImageRules.IsWithinSizeLimit(BackgroundTestImages.FiveMegabytes + 1));
    }

    [Theory]
    [InlineData(7680, 4320, true)]
    [InlineData(8192, 4050, true)]
    [InlineData(8192, 4051, false)]
    [InlineData(7680, 4321, false)]
    [InlineData(8192, 8192, false)]
    [InlineData(8193, 1, false)]
    [InlineData(1, 8193, false)]
    public void IsWithinPixelLimit_EachSideAndTheTotalAreCapped(int width, int height, bool expected)
    {
        Assert.Equal(expected, BackgroundImageRules.IsWithinPixelLimit(width, height));
    }

    [Fact]
    public void TryReadPixelSize_PngWidthPastIntMaxValue_IsUnreadable()
    {
        // -1 is 0xFFFFFFFF as the unsigned width in the header.
        Assert.False(BackgroundImageRules.TryReadPixelSize("image/png", BackgroundTestImages.Png(-1, 1), out _, out _));
    }

    [Fact]
    public void TryReadPixelSize_Png_ReadsWidthAndHeightFromTheHeader()
    {
        var found = BackgroundImageRules.TryReadPixelSize("image/png", BackgroundTestImages.Png(1920, 1080), out var width, out var height);

        Assert.True(found);
        Assert.Equal(1920, width);
        Assert.Equal(1080, height);
    }

    [Fact]
    public void TryReadPixelSize_PngTooShortForAHeader_IsUnreadable()
    {
        var found = BackgroundImageRules.TryReadPixelSize("image/png", BackgroundTestImages.PngSignature, out var width, out var height);

        Assert.False(found);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    [Fact]
    public void TryReadPixelSize_PngWhoseFirstChunkIsNotIhdr_IsUnreadable()
    {
        var bytes = BackgroundTestImages.Png();
        "tEXt"u8.CopyTo(bytes.AsSpan(12));

        Assert.False(BackgroundImageRules.TryReadPixelSize("image/png", bytes, out _, out _));
    }

    [Fact]
    public void TryReadPixelSize_Jpeg_ReadsSizeAfterATable()
    {
        var found = BackgroundImageRules.TryReadPixelSize("image/jpeg", BackgroundTestImages.Jpeg(640, 480), out var width, out var height);

        Assert.True(found);
        Assert.Equal(640, width);
        Assert.Equal(480, height);
    }

    [Fact]
    public void TryReadPixelSize_JpegWithoutStartOfFrame_IsUnreadable()
    {
        Assert.False(BackgroundImageRules.TryReadPixelSize("image/jpeg", BackgroundTestImages.JpegWithoutFrame(), out _, out _));
    }

    [Fact]
    public void TryReadPixelSize_TruncatedJpeg_IsUnreadable()
    {
        byte[] truncated = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B];

        Assert.False(BackgroundImageRules.TryReadPixelSize("image/jpeg", truncated, out _, out _));
    }

    [Fact]
    public void TryReadPixelSize_UnsupportedType_IsUnreadable()
    {
        Assert.False(BackgroundImageRules.TryReadPixelSize("image/gif", BackgroundTestImages.Png(), out _, out _));
    }

    [Fact]
    public void Sha256Of_ReturnsTheLowercaseHexDigest()
    {
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            BackgroundImageRules.Sha256Of(Encoding.ASCII.GetBytes("abc")));
    }
}
