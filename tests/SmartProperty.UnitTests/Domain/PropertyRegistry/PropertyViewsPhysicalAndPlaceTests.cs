using SmartProperty.Domain.Locations;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.PropertyRegistry;

/// <summary>
/// Covers the views, the physical facts, and the place of a property.
/// </summary>
public sealed class PropertyViewsPhysicalAndPlaceTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LaterAt = CreatedAt.AddDays(1);
    private static readonly PropertyAddress Address = new("EG");

    private static readonly PropertyView Sea = new(Guid.NewGuid(), "Sea", "بحر", "Sea", 7);
    private static readonly PropertyView City = new(Guid.NewGuid(), "City", "مدينة", "City", 1);

    private static readonly Area Helwan = SmartProperty.UnitTests.Application.PropertyRegistry.AiTestProperties.Helwan;
    private static readonly Area Maadi = SmartProperty.UnitTests.Application.PropertyRegistry.AiTestProperties.Maadi;
    private static readonly Compound GardenResidence = SmartProperty.UnitTests.Application.PropertyRegistry.AiTestProperties.GardenResidence;

    private static readonly PropertyPhysicalInfo FullPhysical = new(180m, 3, true);

    [Fact]
    public void ANewPropertyHasEmptyPhysicalFactsAndNoViews()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt);

        Assert.Equal(PropertyPhysicalInfo.Empty, property.Physical);
        Assert.Empty(property.Views);
    }

    [Fact]
    public void Constructor_RetainsThePhysicalFactsAndTheViewsWithoutDuplicates()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Villa, Address, CreatedAt, physical: FullPhysical, views: [Sea, City, Sea]);

        Assert.Equal(FullPhysical, property.Physical);
        Assert.Equal(2, property.Views.Count);
        Assert.Contains(Sea, property.Views);
        Assert.Contains(City, property.Views);
    }

    [Fact]
    public void UpdateDetails_ReplacesThePhysicalFactsAndReportsAChange()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt);

        var changed = property.UpdateDetails(PropertyType.Apartment, Address, LaterAt, physical: FullPhysical);

        Assert.True(changed);
        Assert.Equal(FullPhysical, property.Physical);
        Assert.Equal(LaterAt, property.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_LeavingThePhysicalFactsNullClearsThem()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, physical: FullPhysical);

        var changed = property.UpdateDetails(PropertyType.Apartment, Address, LaterAt);

        Assert.True(changed);
        Assert.Equal(PropertyPhysicalInfo.Empty, property.Physical);
    }

    [Fact]
    public void UpdateDetails_TheSamePhysicalFactsAreNotAChange()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, physical: FullPhysical);

        var changed = property.UpdateDetails(
            PropertyType.Apartment, Address, LaterAt, physical: new PropertyPhysicalInfo(180m, 3, true));

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
}
