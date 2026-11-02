using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.PropertyRegistry;

public sealed class PropertyListingInfoTests
{
    [Fact]
    public void Empty_HoldsNothingAndIsAvailable()
    {
        var empty = PropertyListingInfo.Empty;

        Assert.Null(empty.TransactionType);
        Assert.Null(empty.Price);
        Assert.Null(empty.AreaSqm);
        Assert.Null(empty.Floor);
        Assert.Null(empty.Furnished);
        Assert.Equal(ListingStatus.Available, empty.ListingStatus);
        Assert.Null(empty.TitleAr);
        Assert.Null(empty.TitleEn);
        Assert.Null(empty.DescriptionAr);
        Assert.Null(empty.DescriptionEn);
    }

    [Fact]
    public void Constructor_RetainsEveryValue()
    {
        var info = new PropertyListingInfo(
            TransactionType.Rent, 25000m, 120.5m, 3, true, ListingStatus.Reserved, "عنوان", "Title", "وصف", "Description");

        Assert.Equal(TransactionType.Rent, info.TransactionType);
        Assert.Equal(25000m, info.Price);
        Assert.Equal(120.5m, info.AreaSqm);
        Assert.Equal(3, info.Floor);
        Assert.True(info.Furnished);
        Assert.Equal(ListingStatus.Reserved, info.ListingStatus);
        Assert.Equal("عنوان", info.TitleAr);
        Assert.Equal("Title", info.TitleEn);
        Assert.Equal("وصف", info.DescriptionAr);
        Assert.Equal("Description", info.DescriptionEn);
    }

    [Fact]
    public void Constructor_TrimsTextAndTurnsBlankIntoNull()
    {
        var info = new PropertyListingInfo(titleAr: "  عنوان  ", titleEn: "   ", descriptionEn: " text ");

        Assert.Equal("عنوان", info.TitleAr);
        Assert.Null(info.TitleEn);
        Assert.Equal("text", info.DescriptionEn);
    }

    [Fact]
    public void Records_AreComparedByValue()
    {
        Assert.Equal(
            new PropertyListingInfo(TransactionType.Sale, 100m, 50m),
            new PropertyListingInfo(TransactionType.Sale, 100m, 50m));
        Assert.NotEqual(
            new PropertyListingInfo(TransactionType.Sale, 100m, 50m),
            new PropertyListingInfo(TransactionType.Sale, 101m, 50m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_RejectsAPriceThatIsNotPositive(int price)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyListingInfo(price: price));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100001)]
    public void Constructor_RejectsAnAreaOutsideTheSupportedRange(int area)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyListingInfo(areaSqm: area));
    }

    [Fact]
    public void Constructor_AcceptsTheLargestSupportedArea()
    {
        Assert.Equal(PropertyListingInfo.MaxAreaSqm, new PropertyListingInfo(areaSqm: PropertyListingInfo.MaxAreaSqm).AreaSqm);
    }

    [Theory]
    [InlineData(PropertyListingInfo.MinFloor - 1)]
    [InlineData(PropertyListingInfo.MaxFloor + 1)]
    public void Constructor_RejectsAFloorOutsideTheSupportedRange(int floor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyListingInfo(floor: floor));
    }

    [Theory]
    [InlineData(PropertyListingInfo.MinFloor)]
    [InlineData(0)]
    [InlineData(PropertyListingInfo.MaxFloor)]
    public void Constructor_AcceptsTheBoundaryFloors(int floor)
    {
        Assert.Equal(floor, new PropertyListingInfo(floor: floor).Floor);
    }

    [Fact]
    public void Constructor_RejectsUndefinedEnumValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyListingInfo(transactionType: (TransactionType)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyListingInfo(listingStatus: (ListingStatus)9));
    }

    [Fact]
    public void Constructor_RejectsTextBeyondTheLimits()
    {
        Assert.Throws<ArgumentException>(() =>
            new PropertyListingInfo(titleAr: new string('t', PropertyListingInfo.MaxTitleLength + 1)));
        Assert.Throws<ArgumentException>(() =>
            new PropertyListingInfo(descriptionEn: new string('d', PropertyListingInfo.MaxDescriptionLength + 1)));

        _ = new PropertyListingInfo(titleEn: new string('t', PropertyListingInfo.MaxTitleLength));
        _ = new PropertyListingInfo(descriptionAr: new string('d', PropertyListingInfo.MaxDescriptionLength));
    }
}
