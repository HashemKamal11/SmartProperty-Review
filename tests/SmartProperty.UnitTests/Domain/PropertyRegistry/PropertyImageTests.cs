using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.PropertyRegistry;

public sealed class PropertyImageTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid PropertyId = Guid.NewGuid();

    [Fact]
    public void Constructor_RetainsEveryValue()
    {
        var id = Guid.NewGuid();

        var image = new PropertyImage(id, PropertyId, $"{PropertyId:N}/{id:N}.jpg", "front.jpg", PropertyImage.Jpeg, 1234, 2, At);

        Assert.Equal(id, image.Id);
        Assert.Equal(PropertyId, image.PropertyId);
        Assert.Equal($"{PropertyId:N}/{id:N}.jpg", image.StorageKey);
        Assert.Equal("front.jpg", image.FileName);
        Assert.Equal(PropertyImage.Jpeg, image.ContentType);
        Assert.Equal(1234, image.SizeBytes);
        Assert.Equal(2, image.SortOrder);
        Assert.Equal(At, image.CreatedAt);
    }

    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("  photo.jpg  ", "photo.jpg")]
    [InlineData("C:\\Users\\me\\Pictures\\photo.jpg", "photo.jpg")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("folder/sub/photo.png", "photo.png")]
    [InlineData("صورة.jpg", "صورة.jpg")]
    [InlineData("folder/", "image")]
    [InlineData("", "image")]
    [InlineData("   ", "image")]
    [InlineData(null, "image")]
    public void Constructor_KeepsOnlyTheLastSegmentOfTheFileName(string? given, string expected)
    {
        var image = Create(fileName: given);

        Assert.Equal(expected, image.FileName);
    }

    [Fact]
    public void Constructor_CutsAnOverlongFileName()
    {
        var image = Create(fileName: new string('n', PropertyImage.MaxFileNameLength + 50));

        Assert.Equal(PropertyImage.MaxFileNameLength, image.FileName.Length);
    }

    [Theory]
    [InlineData(PropertyImage.Jpeg, ".jpg")]
    [InlineData(PropertyImage.Png, ".png")]
    [InlineData(PropertyImage.WebP, ".webp")]
    public void ExtensionOf_NamesEverySupportedType(string contentType, string extension)
    {
        Assert.Equal(extension, PropertyImage.ExtensionOf(contentType));
    }

    [Fact]
    public void ExtensionOf_RejectsAnUnsupportedType()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PropertyImage.ExtensionOf("image/gif"));
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("image/svg+xml")]
    [InlineData("IMAGE/JPEG")]
    [InlineData("text/html")]
    [InlineData("")]
    public void Constructor_RejectsAnyOtherContentType(string contentType)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(contentType: contentType));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PropertyImage.MaxSizeBytes + 1)]
    public void Constructor_RejectsASizeOutsideTheLimits(long size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(size: size));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(PropertyImage.MaxSizeBytes)]
    public void Constructor_AcceptsTheBoundarySizes(long size)
    {
        Assert.Equal(size, Create(size: size).SizeBytes);
    }

    [Fact]
    public void Constructor_RejectsEmptyIdsAndABlankOrOverlongKey()
    {
        Assert.Throws<ArgumentException>(() => new PropertyImage(Guid.Empty, PropertyId, "k", "a.jpg", PropertyImage.Jpeg, 1, 0, At));
        Assert.Throws<ArgumentException>(() => new PropertyImage(Guid.NewGuid(), Guid.Empty, "k", "a.jpg", PropertyImage.Jpeg, 1, 0, At));
        Assert.Throws<ArgumentException>(() => Create(key: " "));
        Assert.Throws<ArgumentException>(() => Create(key: new string('k', PropertyImage.MaxStorageKeyLength + 1)));
    }

    [Fact]
    public void Constructor_RejectsANegativeSortOrder()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(sortOrder: -1));
    }

    [Fact]
    public void MoveTo_ChangesTheSortOrderAndRejectsANegativeOne()
    {
        var image = Create(sortOrder: 5);

        image.MoveTo(0);

        Assert.Equal(0, image.SortOrder);
        Assert.Throws<ArgumentOutOfRangeException>(() => image.MoveTo(-3));
    }

    [Fact]
    public void AProperty_StartsWithNoImagesAndAUpdateOfItsDetailsNeverTouchesThem()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), At);

        Assert.Empty(property.Images);
        Assert.True(property.UpdateDetails(PropertyType.Villa, new PropertyAddress("EG"), At.AddDays(1)));
        Assert.Empty(property.Images);
    }

    private static PropertyImage Create(
        string? fileName = "a.jpg",
        string contentType = PropertyImage.Jpeg,
        long size = 100,
        string key = "key/a.jpg",
        int sortOrder = 0)
    {
        return new PropertyImage(Guid.NewGuid(), PropertyId, key, fileName, contentType, size, sortOrder, At);
    }
}
