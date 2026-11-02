using SmartProperty.Application.PropertyRegistry.Ai;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;
using static SmartProperty.UnitTests.Application.PropertyRegistry.AiTestProperties;

namespace SmartProperty.UnitTests.Application.PropertyRegistry;

public sealed class AiListingMapperTests
{
    [Fact]
    public void ACompleteProperty_HasNoBlockers()
    {
        Assert.Empty(AiListingMapper.Blockers(Complete()));
    }

    [Fact]
    public void ABareProperty_IsBlockedByEverythingTheAiServiceRequires()
    {
        var blockers = AiListingMapper.Blockers(Bare());

        var blocker = Assert.Single(blockers);
        Assert.Equal("missing transactionType, price, areaSqm, areaId, bedrooms, bathrooms", blocker);
    }

    [Theory]
    [InlineData(50, 50, 0)]
    [InlineData(51, 50, 1)]
    [InlineData(50, 51, 1)]
    [InlineData(100, 100, 2)]
    public void TheAiRoomLimitBlocksOnlyAboveFifty(int bedrooms, int bathrooms, int expectedBlockers)
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Building, new PropertyAddress("EG"), CreatedAt,
            bedrooms: bedrooms,
            bathrooms: bathrooms,
            listing: new PropertyListingInfo(TransactionType.Rent, 10m, 10m),
            area: Helwan);

        Assert.Equal(expectedBlockers, AiListingMapper.Blockers(property).Count);
    }

    [Fact]
    public void ToListing_UsesTheAiVocabularyAndEveryFact()
    {
        var property = Complete(amenities: [Balcony], views: [City]);

        var listing = AiListingMapper.ToListing(property);

        Assert.Equal(12, listing.AreaId);
        Assert.Equal(7, listing.CompoundId);
        Assert.Equal("Twin House", listing.PropertyType);
        Assert.Equal("Sale", listing.TransactionType);
        Assert.Equal(6_250_000.50m, listing.Price);
        Assert.Equal(210.5m, listing.AreaSqm);
        Assert.Equal(4, listing.Bedrooms);
        Assert.Equal(3, listing.Bathrooms);
        Assert.Equal(2, listing.Floor);
        Assert.Equal("Luxury Finished", listing.FinishingStatus);
        Assert.True(listing.Furnished);
        Assert.Equal("Reserved", listing.ListingStatus);
        Assert.Equal("عنوان", listing.TitleAr);
        Assert.Equal("Title", listing.TitleEn);
        Assert.Equal("وصف", listing.DescriptionAr);
        Assert.Equal("Description", listing.DescriptionEn);
    }

    [Fact]
    public void ToListing_LeavesOptionalFactsNullWhenNotStated()
    {
        var property = Complete(finishing: null, inCompound: false);

        var listing = AiListingMapper.ToListing(property);

        Assert.Null(listing.CompoundId);
        Assert.Null(listing.FinishingStatus);
        Assert.Null(listing.View);
        Assert.Empty(listing.Amenities);
    }

    [Fact]
    public void ToListing_SendsTheFirstViewInDisplayOrder()
    {
        var listing = AiListingMapper.ToListing(Complete(views: [Sea, City]));

        Assert.Equal("City", listing.View);
    }

    [Fact]
    public void ToListing_SendsEachAiAmenityCodeOnceInDisplayOrder()
    {
        var property = Complete(amenities: [SharedGym, PrivatePool, Pool, Balcony]);

        var listing = AiListingMapper.ToListing(property);

        Assert.Equal(["balcony", "pool", "gym"], listing.Amenities);
    }

    [Fact]
    public void ToListing_SkipsAmenitiesThatHaveNoAiEquivalent()
    {
        var listing = AiListingMapper.ToListing(Complete(amenities: [Barbecue, Balcony]));

        Assert.Equal(["balcony"], listing.Amenities);
    }
}
