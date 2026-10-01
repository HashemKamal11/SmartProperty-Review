using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.PropertyRegistry;

/// <summary>
/// Covers the canonical asset identity: construction invariants and the archive transition. Every time value
/// is supplied by the test; nothing here reads a machine clock.
/// </summary>
public sealed class PropertyTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_RetainsTheSuppliedIdentity()
    {
        var id = Guid.NewGuid();

        var property = CreateProperty(id: id);

        Assert.Equal(id, property.Id);
    }

    [Theory]
    [InlineData(PropertyType.Land)]
    [InlineData(PropertyType.Building)]
    [InlineData(PropertyType.Unit)]
    public void Constructor_RetainsThePhysicalForm(PropertyType type)
    {
        var property = CreateProperty(type: type);

        Assert.Equal(type, property.Type);
    }

    [Fact]
    public void Constructor_RetainsTheAddress()
    {
        var address = new PropertyAddress("SA", city: "Riyadh");

        var property = CreateProperty(address: address);

        Assert.Same(address, property.Address);
    }

    [Fact]
    public void Constructor_NormalizesTheAddressItReceives()
    {
        var property = CreateProperty(address: new PropertyAddress("  sa  ", city: "  Riyadh  "));

        Assert.Equal("SA", property.Address.CountryCode);
        Assert.Equal("Riyadh", property.Address.City);
    }

    [Fact]
    public void Constructor_StartsTheLifecycleAsActive()
    {
        var property = CreateProperty();

        Assert.Equal(PropertyStatus.Active, property.Status);
    }

    [Fact]
    public void Constructor_SetsBothTimestampsToTheCreationInstant()
    {
        var property = CreateProperty();

        Assert.Equal(CreatedAt, property.CreatedAt);
        Assert.Equal(CreatedAt, property.UpdatedAt);
    }

    [Fact]
    public void Constructor_RejectsEmptyId()
    {
        Assert.Throws<ArgumentException>(() => CreateProperty(id: Guid.Empty));
    }

    [Fact]
    public void Constructor_RejectsAnUndefinedPropertyType()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateProperty(type: (PropertyType)0));
    }

    [Fact]
    public void Constructor_RejectsAMissingAddress()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Property(Guid.NewGuid(), PropertyType.Land, null!, CreatedAt));
    }

    [Fact]
    public void Archive_MovesAnActivePropertyToArchived()
    {
        var property = CreateProperty();
        var archivedAt = CreatedAt.AddDays(1);

        property.Archive(archivedAt);

        Assert.Equal(PropertyStatus.Archived, property.Status);
        Assert.Equal(archivedAt, property.UpdatedAt);
    }

    [Fact]
    public void Archive_LeavesTheCreationInstantUntouched()
    {
        var property = CreateProperty();

        property.Archive(CreatedAt.AddDays(1));

        Assert.Equal(CreatedAt, property.CreatedAt);
    }

    [Fact]
    public void Archive_AllowsArchivingAtTheCreationInstant()
    {
        var property = CreateProperty();

        property.Archive(CreatedAt);

        Assert.Equal(PropertyStatus.Archived, property.Status);
    }

    [Fact]
    public void Archive_RejectsASecondArchive()
    {
        var property = CreateProperty();
        property.Archive(CreatedAt.AddDays(1));

        Assert.Throws<InvalidOperationException>(() => property.Archive(CreatedAt.AddDays(2)));
    }

    [Fact]
    public void Archive_RejectsATimeBeforeTheCurrentUpdate()
    {
        var property = CreateProperty();

        Assert.Throws<ArgumentException>(() => property.Archive(CreatedAt.AddSeconds(-1)));
    }

    private static Property CreateProperty(
        Guid? id = null,
        PropertyType type = PropertyType.Land,
        PropertyAddress? address = null)
    {
        return new Property(
            id ?? Guid.NewGuid(),
            type,
            address ?? new PropertyAddress("SA"),
            CreatedAt);
    }
}
