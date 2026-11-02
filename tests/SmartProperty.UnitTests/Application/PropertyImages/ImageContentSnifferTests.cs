using SmartProperty.Application.PropertyImages;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Application.PropertyImages;

public sealed class ImageContentSnifferTests
{
    [Fact]
    public void ARealJpegPngAndWebPAreRecognisedByTheirBytes()
    {
        Assert.Equal(PropertyImage.Jpeg, ImageContentSniffer.Detect(TestImages.Jpeg()));
        Assert.Equal(PropertyImage.Png, ImageContentSniffer.Detect(TestImages.Png()));
        Assert.Equal(PropertyImage.WebP, ImageContentSniffer.Detect(TestImages.WebP()));
    }

    [Fact]
    public void OnlyTheFirstTwelveBytesAreNeeded()
    {
        Assert.Equal(PropertyImage.Png, ImageContentSniffer.Detect(TestImages.Png(12).AsSpan(0, 12)));
        Assert.Equal(PropertyImage.WebP, ImageContentSniffer.Detect(TestImages.WebP(12).AsSpan(0, 12)));
    }

    [Fact]
    public void OtherFormatsAndScriptsAreNotImages()
    {
        Assert.Null(ImageContentSniffer.Detect(TestImages.Gif()));
        Assert.Null(ImageContentSniffer.Detect(TestImages.Html()));
        Assert.Null(ImageContentSniffer.Detect("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8));
        Assert.Null(ImageContentSniffer.Detect("BM"u8));
        Assert.Null(ImageContentSniffer.Detect("MZ\u0090\u0000"u8));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ABufferTooShortToHoldAMagicNumberIsNotAnImage(int length)
    {
        Assert.Null(ImageContentSniffer.Detect(TestImages.Jpeg().AsSpan(0, length)));
    }

    [Fact]
    public void ARiffFileThatIsNotWebPIsNotAnImage()
    {
        var wave = "RIFF"u8.ToArray().Concat(new byte[] { 0x24, 0, 0, 0 }).Concat("WAVE"u8.ToArray()).ToArray();

        Assert.Null(ImageContentSniffer.Detect(wave));
    }

    [Fact]
    public void ATruncatedPngSignatureIsNotAnImage()
    {
        var almost = TestImages.Png().AsSpan(0, 7);

        Assert.Null(ImageContentSniffer.Detect(almost));
    }
}
