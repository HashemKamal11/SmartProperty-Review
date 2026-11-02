using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.PropertyRegistry;

/// <summary>
/// Covers the optional specification fields of a property: bedrooms, bathrooms, built date, and finishing status.
/// </summary>
public sealed class PropertySpecificationsTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LaterAt = CreatedAt.AddDays(1);
    private static readonly PropertyAddress Address = new("EG");

    [Fact]
    public void Constructor_LeavesEverySpecificationNullWhenNoneIsSupplied()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Land, Address, CreatedAt);

        Assert.Null(property.Bedrooms);
        Assert.Null(property.Bathrooms);
        Assert.Null(property.BuiltDate);
        Assert.Null(property.FinishingStatus);
    }

    [Fact]
    public void Constructor_RetainsEverySuppliedSpecification()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, 3, 2, new DateOnly(2020, 5, 1), FinishingStatus.LuxuryFinished);

        Assert.Equal(3, property.Bedrooms);
        Assert.Equal(2, property.Bathrooms);
        Assert.Equal(new DateOnly(2020, 5, 1), property.BuiltDate);
        Assert.Equal(FinishingStatus.LuxuryFinished, property.FinishingStatus);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(Property.MaxRoomCount)]
    public void Constructor_AcceptsTheBoundaryRoomCounts(int count)
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, bedrooms: count, bathrooms: count);

        Assert.Equal(count, property.Bedrooms);
        Assert.Equal(count, property.Bathrooms);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(Property.MaxRoomCount + 1)]
    public void Constructor_RejectsAnOutOfRangeBedroomCount(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, bedrooms: count));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(Property.MaxRoomCount + 1)]
    public void Constructor_RejectsAnOutOfRangeBathroomCount(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, bathrooms: count));
    }

    [Fact]
    public void Constructor_RejectsABuiltDateBeforeTheEarliestSupported()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Property(
                Guid.NewGuid(), PropertyType.Building, Address, CreatedAt,
                builtDate: Property.EarliestBuiltDate.AddDays(-1)));
    }

    [Fact]
    public void Constructor_AcceptsAFutureBuiltDateForOffPlanProperties()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Building, Address, CreatedAt, builtDate: new DateOnly(2030, 1, 1));

        Assert.Equal(new DateOnly(2030, 1, 1), property.BuiltDate);
    }

    [Fact]
    public void Constructor_RejectsAnUndefinedFinishingStatus()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Property(
                Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, finishingStatus: (FinishingStatus)99));
    }

    [Fact]
    public void UpdateDetails_ReplacesEverySpecificationAndReportsAChange()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt);

        var changed = property.UpdateDetails(
            PropertyType.Apartment, Address, LaterAt, 4, 3, new DateOnly(2018, 3, 9), FinishingStatus.LuxuryFinished);

        Assert.True(changed);
        Assert.Equal(4, property.Bedrooms);
        Assert.Equal(3, property.Bathrooms);
        Assert.Equal(new DateOnly(2018, 3, 9), property.BuiltDate);
        Assert.Equal(FinishingStatus.LuxuryFinished, property.FinishingStatus);
        Assert.Equal(LaterAt, property.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_ClearsASpecificationLeftNull()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, 3, 2, new DateOnly(2020, 5, 1), FinishingStatus.LuxuryFinished);

        var changed = property.UpdateDetails(PropertyType.Apartment, Address, LaterAt);

        Assert.True(changed);
        Assert.Null(property.Bedrooms);
        Assert.Null(property.Bathrooms);
        Assert.Null(property.BuiltDate);
        Assert.Null(property.FinishingStatus);
    }

    [Fact]
    public void UpdateDetails_ChangingOnlyASpecificationIsAChange()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, finishingStatus: FinishingStatus.CoreAndShell);

        var changed = property.UpdateDetails(
            PropertyType.Apartment, Address, LaterAt, finishingStatus: FinishingStatus.Finished);

        Assert.True(changed);
        Assert.Equal(LaterAt, property.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_IdenticalSpecificationsAreNotAChange()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, 3, 2, new DateOnly(2020, 5, 1), FinishingStatus.LuxuryFinished);

        var changed = property.UpdateDetails(
            PropertyType.Apartment, Address, LaterAt, 3, 2, new DateOnly(2020, 5, 1), FinishingStatus.LuxuryFinished);

        Assert.False(changed);
        Assert.Equal(CreatedAt, property.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_RejectsInvalidSpecificationsAndLeavesThePropertyUntouched()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt, bedrooms: 2);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            property.UpdateDetails(PropertyType.Apartment, Address, LaterAt, bedrooms: -5));

        Assert.Equal(2, property.Bedrooms);
        Assert.Equal(CreatedAt, property.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_RefusesAnArchivedPropertyEvenWithNewSpecifications()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Apartment, Address, CreatedAt);
        property.Archive(CreatedAt);

        Assert.Throws<InvalidOperationException>(() =>
            property.UpdateDetails(PropertyType.Apartment, Address, LaterAt, bedrooms: 3));
    }

    [Theory]
    [InlineData(FinishingStatus.Finished, 1)]
    [InlineData(FinishingStatus.LuxuryFinished, 2)]
    [InlineData(FinishingStatus.SemiFinished, 3)]
    [InlineData(FinishingStatus.CoreAndShell, 4)]
    public void FinishingStatus_KeepsItsStableNumericValues(FinishingStatus status, int expected)
    {
        Assert.Equal(expected, (int)status);
    }
}
