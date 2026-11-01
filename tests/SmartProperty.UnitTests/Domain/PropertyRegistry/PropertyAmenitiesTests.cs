using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.PropertyRegistry;

public sealed class PropertyAmenitiesTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LaterAt = CreatedAt.AddDays(1);
    private static readonly PropertyAddress Address = new("EG");

    private static readonly Amenity Pool = new(Guid.NewGuid(), "SwimmingPool", "مسبح", "Swimming pool", 2);
    private static readonly Amenity Gym = new(Guid.NewGuid(), "SharedGym", "نادي رياضي عام", "Shared gym", 1);
    private static readonly Amenity Balcony = new(Guid.NewGuid(), "Balcony", "شرفة", "Balcony", 0);

    [Fact]
    public void Amenity_TrimsItsTextAndRetainsEveryValue()
    {
        var id = Guid.NewGuid();

        var amenity = new Amenity(id, "  Balcony ", " شرفة ", " Balcony ", 7);

        Assert.Equal(id, amenity.Id);
        Assert.Equal("Balcony", amenity.Code);
        Assert.Equal("شرفة", amenity.NameAr);
        Assert.Equal("Balcony", amenity.NameEn);
        Assert.Equal(7, amenity.SortOrder);
    }

    [Fact]
    public void Amenity_RejectsAnEmptyId()
    {
        Assert.Throws<ArgumentException>(() => new Amenity(Guid.Empty, "Balcony", "شرفة", "Balcony", 0));
    }

    [Theory]
    [InlineData("", "شرفة", "Balcony")]
    [InlineData("   ", "شرفة", "Balcony")]
    [InlineData("Balcony", "", "Balcony")]
    [InlineData("Balcony", "شرفة", "  ")]
    public void Amenity_RejectsBlankText(string code, string nameAr, string nameEn)
    {
        Assert.Throws<ArgumentException>(() => new Amenity(Guid.NewGuid(), code, nameAr, nameEn, 0));
    }

    [Fact]
    public void Amenity_RejectsATooLongCode()
    {
        Assert.Throws<ArgumentException>(() =>
            new Amenity(Guid.NewGuid(), new string('c', Amenity.MaxCodeLength + 1), "شرفة", "Balcony", 0));
    }

    [Fact]
    public void Constructor_WithoutAmenitiesLeavesTheSetEmpty()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Unit, Address, CreatedAt);

        Assert.Empty(property.Amenities);
    }

    [Fact]
    public void Constructor_RetainsTheSuppliedAmenitiesAndCollapsesDuplicates()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Unit, Address, CreatedAt, amenities: [Pool, Gym, Pool]);

        Assert.Equal(2, property.Amenities.Count);
        Assert.Contains(Pool, property.Amenities);
        Assert.Contains(Gym, property.Amenities);
    }

    [Fact]
    public void UpdateDetails_ReplacesTheAmenitiesAndReportsAChange()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Unit, Address, CreatedAt, amenities: [Pool, Gym]);

        var changed = property.UpdateDetails(PropertyType.Unit, Address, LaterAt, amenities: [Gym, Balcony]);

        Assert.True(changed);
        Assert.Equal(2, property.Amenities.Count);
        Assert.Contains(Gym, property.Amenities);
        Assert.Contains(Balcony, property.Amenities);
        Assert.DoesNotContain(Pool, property.Amenities);
        Assert.Equal(LaterAt, property.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_LeavingAmenitiesNullClearsThem()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Unit, Address, CreatedAt, amenities: [Pool]);

        var changed = property.UpdateDetails(PropertyType.Unit, Address, LaterAt);

        Assert.True(changed);
        Assert.Empty(property.Amenities);
    }

    [Fact]
    public void UpdateDetails_ChangingOnlyTheAmenitiesIsAChange()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Unit, Address, CreatedAt);

        var changed = property.UpdateDetails(PropertyType.Unit, Address, LaterAt, amenities: [Pool]);

        Assert.True(changed);
        Assert.Equal(LaterAt, property.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_TheSameAmenitiesInAnyOrderAreNotAChange()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Unit, Address, CreatedAt, amenities: [Pool, Gym]);

        var changed = property.UpdateDetails(PropertyType.Unit, Address, LaterAt, amenities: [Gym, Pool, Gym]);

        Assert.False(changed);
        Assert.Equal(CreatedAt, property.UpdatedAt);
    }
}
