using SmartProperty.Domain.Locations;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.PropertyRegistry;

/// <summary>
/// Covers the views, the market-facing listing facts, and the AI sync bookkeeping of a property.
/// </summary>
public sealed class PropertyAiSyncTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LaterAt = CreatedAt.AddDays(1);
    private static readonly PropertyAddress Address = new("EG");

    private static readonly PropertyView Sea = new(Guid.NewGuid(), "Sea", "بحر", "Sea", 7);
    private static readonly PropertyView City = new(Guid.NewGuid(), "City", "مدينة", "City", 1);

    private static readonly Area Helwan = SmartProperty.UnitTests.Application.PropertyRegistry.AiTestProperties.Helwan;
    private static readonly Area Maadi = SmartProperty.UnitTests.Application.PropertyRegistry.AiTestProperties.Maadi;
    private static readonly Compound GardenResidence = SmartProperty.UnitTests.Application.PropertyRegistry.AiTestProperties.GardenResidence;

    private static readonly PropertyListingInfo FullListing = new(TransactionType.Sale, 4_500_000m, 180m);

    [Fact]
    public void ANewPropertyIsNotSyncedAndHasAnEmptyListing()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt);

        Assert.Equal(AiSyncStatus.NotSynced, property.AiSyncStatus);
        Assert.Null(property.AiSyncedAt);
        Assert.Null(property.AiSyncError);
        Assert.Equal(PropertyListingInfo.Empty, property.Listing);
        Assert.Empty(property.Views);
    }

    [Fact]
    public void Constructor_RetainsTheListingAndTheViewsWithoutDuplicates()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, Address, CreatedAt, listing: FullListing, views: [Sea, City, Sea]);

        Assert.Equal(FullListing, property.Listing);
        Assert.Equal(2, property.Views.Count);
        Assert.Contains(Sea, property.Views);
        Assert.Contains(City, property.Views);
    }

    [Fact]
    public void UpdateDetails_ReplacesTheListingAndReportsAChange()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt);

        var changed = property.UpdateDetails(PropertyType.Apartment, Address, LaterAt, listing: FullListing);

        Assert.True(changed);
        Assert.Equal(FullListing, property.Listing);
        Assert.Equal(LaterAt, property.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_LeavingTheListingNullClearsIt()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, listing: FullListing);

        var changed = property.UpdateDetails(PropertyType.Apartment, Address, LaterAt);

        Assert.True(changed);
        Assert.Equal(PropertyListingInfo.Empty, property.Listing);
    }

    [Fact]
    public void UpdateDetails_TheSameListingIsNotAChange()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, listing: FullListing);

        var changed = property.UpdateDetails(
            PropertyType.Apartment, Address, LaterAt, listing: new PropertyListingInfo(TransactionType.Sale, 4_500_000m, 180m));

        Assert.False(changed);
        Assert.Equal(CreatedAt, property.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_ReplacesTheViewsAndIgnoresTheirOrder()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, views: [Sea]);

        Assert.True(property.UpdateDetails(PropertyType.Apartment, Address, LaterAt, views: [City]));
        Assert.Equal([City], property.Views);

        var both = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, views: [Sea, City]);
        Assert.False(both.UpdateDetails(PropertyType.Apartment, Address, LaterAt, views: [City, Sea]));
    }

    [Fact]
    public void MissingForAiSync_NamesEverythingTheAiServiceRequires()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt);

        Assert.Equal(
            ["transactionType", "price", "areaSqm", "areaId", "bedrooms", "bathrooms"],
            property.MissingForAiSync());
    }

    [Fact]
    public void MissingForAiSync_IsEmptyWhenTheRequiredFactsArePresent()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, bedrooms: 3, bathrooms: 2, listing: FullListing, area: Helwan);

        Assert.Empty(property.MissingForAiSync());
    }

    [Fact]
    public void MissingForAiSync_CountsZeroRoomsAsPresent()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Studio, Address, CreatedAt, bedrooms: 0, bathrooms: 1, listing: FullListing, area: Helwan);

        Assert.Empty(property.MissingForAiSync());
    }

    [Fact]
    public void Constructor_RetainsTheAreaAndTheCompoundAndTheirIds()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, Address, CreatedAt, area: Helwan, compound: GardenResidence);

        Assert.Same(Helwan, property.Area);
        Assert.Equal(Helwan.Id, property.AreaId);
        Assert.Same(GardenResidence, property.Compound);
        Assert.Equal(GardenResidence.Id, property.CompoundId);
    }

    [Fact]
    public void ANewPropertyHasNoPlace()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Villa, Address, CreatedAt);

        Assert.Null(property.Area);
        Assert.Null(property.AreaId);
        Assert.Null(property.Compound);
        Assert.Null(property.CompoundId);
    }

    [Fact]
    public void Constructor_RejectsACompoundWithoutItsArea()
    {
        Assert.Throws<ArgumentException>(() =>
            new Property(Guid.NewGuid(), PropertyType.Villa, Address, CreatedAt, compound: GardenResidence));
    }

    [Fact]
    public void Constructor_RejectsACompoundThatBelongsToAnotherArea()
    {
        Assert.Throws<ArgumentException>(() =>
            new Property(Guid.NewGuid(), PropertyType.Villa, Address, CreatedAt, area: Maadi, compound: GardenResidence));
    }

    [Fact]
    public void UpdateDetails_MovesThePropertyToAnotherAreaAndClearsTheCompound()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, Address, CreatedAt, area: Helwan, compound: GardenResidence);

        var changed = property.UpdateDetails(PropertyType.Villa, Address, LaterAt, area: Maadi);

        Assert.True(changed);
        Assert.Equal(Maadi.Id, property.AreaId);
        Assert.Null(property.CompoundId);
        Assert.Equal(LaterAt, property.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_LeavingThePlaceNullClearsIt()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Villa, Address, CreatedAt, area: Helwan);

        var changed = property.UpdateDetails(PropertyType.Villa, Address, LaterAt);

        Assert.True(changed);
        Assert.Null(property.AreaId);
        Assert.Null(property.Area);
    }

    [Fact]
    public void UpdateDetails_TheSamePlaceIsNotAChange()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, Address, CreatedAt, area: Helwan, compound: GardenResidence);

        var changed = property.UpdateDetails(
            PropertyType.Villa, Address, LaterAt, area: Helwan, compound: GardenResidence);

        Assert.False(changed);
        Assert.Equal(CreatedAt, property.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_AddingOnlyACompoundIsAChange()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Villa, Address, CreatedAt, area: Helwan);

        Assert.True(property.UpdateDetails(
            PropertyType.Villa, Address, LaterAt, area: Helwan, compound: GardenResidence));
        Assert.Equal(GardenResidence.Id, property.CompoundId);
    }

    [Fact]
    public void UpdateDetails_RejectsACompoundWithoutItsAreaAndLeavesThePropertyUntouched()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Villa, Address, CreatedAt, area: Helwan);

        Assert.Throws<ArgumentException>(() =>
            property.UpdateDetails(PropertyType.Villa, Address, LaterAt, compound: GardenResidence));

        Assert.Equal(Helwan.Id, property.AreaId);
        Assert.Equal(CreatedAt, property.UpdatedAt);
    }

    [Fact]
    public void MissingForAiSync_NamesTheAreaWhenOnlyThePlaceIsMissing()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, bedrooms: 3, bathrooms: 2, listing: FullListing);

        Assert.Equal(["areaId"], property.MissingForAiSync());
    }

    [Fact]
    public void MarkAiSynced_RecordsTheInstantAndClearsAnyError()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt);
        property.MarkAiSyncFailed("it broke");

        property.MarkAiSynced(LaterAt);

        Assert.Equal(AiSyncStatus.Synced, property.AiSyncStatus);
        Assert.Equal(LaterAt, property.AiSyncedAt);
        Assert.Null(property.AiSyncError);
    }

    [Fact]
    public void MarkAiSyncFailed_KeepsTheReasonAndTheLastSuccessInstant()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt);
        property.MarkAiSynced(CreatedAt);

        property.MarkAiSyncFailed("  the service timed out  ");

        Assert.Equal(AiSyncStatus.Failed, property.AiSyncStatus);
        Assert.Equal("the service timed out", property.AiSyncError);
        Assert.Equal(CreatedAt, property.AiSyncedAt);
    }

    [Fact]
    public void MarkAiNotSynced_ClearsTheInstantAndKeepsTheReason()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt);
        property.MarkAiSynced(CreatedAt);

        property.MarkAiNotSynced("missing price");

        Assert.Equal(AiSyncStatus.NotSynced, property.AiSyncStatus);
        Assert.Null(property.AiSyncedAt);
        Assert.Equal("missing price", property.AiSyncError);
    }

    [Fact]
    public void MarkAiNotSynced_WithoutAReasonLeavesNoError()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt);
        property.MarkAiSyncFailed("x");

        property.MarkAiNotSynced();

        Assert.Null(property.AiSyncError);
    }

    [Fact]
    public void TheSyncErrorIsCutToTheStoredLength()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt);

        property.MarkAiSyncFailed(new string('e', Property.MaxAiSyncErrorLength + 500));

        Assert.Equal(Property.MaxAiSyncErrorLength, property.AiSyncError!.Length);
    }

    [Fact]
    public void TheBookkeepingNeverMovesTheUpdateInstant()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt);

        property.MarkAiSynced(LaterAt);
        property.MarkAiSyncFailed("x");
        property.MarkAiNotSynced("y");

        Assert.Equal(CreatedAt, property.UpdatedAt);
    }
}
