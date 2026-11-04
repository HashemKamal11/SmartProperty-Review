using SmartProperty.Domain.PropertyRegistration;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;
using static SmartProperty.UnitTests.Domain.PropertyRegistration.PropertyRegistrationFixtures;

namespace SmartProperty.UnitTests.Domain.PropertyRegistration;

/// <summary>
/// Covers the typed details: validation of what is present, conditional applicability by physical type, and the
/// completeness check that gates submission.
/// </summary>
public sealed class PropertyRegistrationRequestDetailsTests
{
    [Fact]
    public void Constructor_AcceptsEmptyDraftShape()
    {
        var details = new PropertyRegistrationRequestDetails();

        Assert.Null(details.Type);
        Assert.Null(details.Address);
        Assert.Null(details.Area);
    }

    [Fact]
    public void Constructor_RetainsSuppliedValues()
    {
        var address = CompleteAddress();

        var details = new PropertyRegistrationRequestDetails(PropertyType.Unit, address, 95.5m, 2, 1, 2010);

        Assert.Equal(PropertyType.Unit, details.Type);
        Assert.Same(address, details.Address);
        Assert.Equal(95.5m, details.Area);
        Assert.Equal(2, details.Bedrooms);
        Assert.Equal(1, details.Bathrooms);
        Assert.Equal(2010, details.ConstructionYear);
    }

    [Fact]
    public void Constructor_RejectsAnUndefinedType()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyRegistrationRequestDetails((PropertyType)99));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsANonPositiveArea(int area)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyRegistrationRequestDetails(area: area));
    }

    [Fact]
    public void Constructor_RejectsNegativeBedroomsAndBathrooms()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyRegistrationRequestDetails(bedrooms: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyRegistrationRequestDetails(bathrooms: -1));
    }

    [Fact]
    public void Constructor_AcceptsZeroBedroomsAndBathrooms()
    {
        var details = new PropertyRegistrationRequestDetails(PropertyType.Unit, bedrooms: 0, bathrooms: 0);

        Assert.Equal(0, details.Bedrooms);
        Assert.Equal(0, details.Bathrooms);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2000)]
    public void Constructor_RejectsANonPositiveConstructionYear(int year)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyRegistrationRequestDetails(constructionYear: year));
    }

    [Fact]
    public void Constructor_RejectsBedroomsAndBathroomsForLand()
    {
        Assert.Throws<ArgumentException>(() => new PropertyRegistrationRequestDetails(PropertyType.Land, bedrooms: 1));
        Assert.Throws<ArgumentException>(() => new PropertyRegistrationRequestDetails(PropertyType.Land, bathrooms: 1));
    }

    [Theory]
    [InlineData(PropertyType.Building)]
    [InlineData(PropertyType.Unit)]
    public void Constructor_AcceptsRoomsAndYearForBuildingAndUnit(PropertyType type)
    {
        var details = new PropertyRegistrationRequestDetails(type, bedrooms: 4, bathrooms: 3, constructionYear: 2001);

        Assert.Equal(4, details.Bedrooms);
        Assert.Equal(3, details.Bathrooms);
        Assert.Equal(2001, details.ConstructionYear);
    }

    [Fact]
    public void Constructor_AcceptsAConstructionYearForLand()
    {
        var details = new PropertyRegistrationRequestDetails(PropertyType.Land, constructionYear: 1999);

        Assert.Equal(1999, details.ConstructionYear);
    }

    [Fact]
    public void GetMissingRequirements_ForAnEmptyDraft_NamesTheCommonRequiredFields()
    {
        var missing = new PropertyRegistrationRequestDetails().GetMissingRequirements();

        Assert.Equal(["Type", "Address", "Area"], missing);
    }

    [Fact]
    public void GetMissingRequirements_NamesMissingAddressComponents()
    {
        var details = new PropertyRegistrationRequestDetails(
            PropertyType.Building,
            new PropertyAddress("SA"),
            800m);

        Assert.Equal(["Address.City", "Address.District", "Address.AddressLine"], details.GetMissingRequirements());
    }

    [Theory]
    [InlineData("   ", "Hittin", "Villa 1", "Address.City")]
    [InlineData("Riyadh", "   ", "Villa 1", "Address.District")]
    [InlineData("Riyadh", "Hittin", "   ", "Address.AddressLine")]
    public void GetMissingRequirements_TreatsWhitespaceAddressComponentsAsMissing(
        string city,
        string district,
        string addressLine,
        string expectedMissing)
    {
        var details = new PropertyRegistrationRequestDetails(
            PropertyType.Building,
            new PropertyAddress("SA", city: city, district: district, addressLine: addressLine),
            800m);

        Assert.Equal([expectedMissing], details.GetMissingRequirements());
    }

    [Fact]
    public void GetMissingRequirements_ForAUnit_RequiresRoomsAndConstructionYear()
    {
        var details = new PropertyRegistrationRequestDetails(PropertyType.Unit, CompleteAddress(), 90m);

        Assert.Equal(["Bedrooms", "Bathrooms", "ConstructionYear"], details.GetMissingRequirements());
    }

    [Theory]
    [InlineData(PropertyType.Land)]
    [InlineData(PropertyType.Building)]
    public void GetMissingRequirements_ForLandAndBuilding_DoesNotRequireRoomsOrYear(PropertyType type)
    {
        var details = new PropertyRegistrationRequestDetails(type, CompleteAddress(), 500m);

        Assert.Empty(details.GetMissingRequirements());
    }

    [Fact]
    public void EnsureReadyForSubmission_AcceptsCompleteDetails()
    {
        var exception = Record.Exception(() => CompleteUnit().EnsureReadyForSubmission());

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureReadyForSubmission_RejectsIncompleteDetailsNamingTheGaps()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new PropertyRegistrationRequestDetails().EnsureReadyForSubmission());

        Assert.Contains("Type", exception.Message);
        Assert.Contains("Area", exception.Message);
    }

    [Fact]
    public void PropertyAddress_ComparesByValue()
    {
        Assert.Equal(CompleteAddress(), CompleteAddress());
        Assert.NotSame(CompleteAddress(), CompleteAddress());
    }

    [Fact]
    public void Details_BuiltFromIdenticalValuesInSeparateInstances_AreEqual()
    {
        var first = new PropertyRegistrationRequestDetails(PropertyType.Unit, CompleteAddress(), 120m, 3, 2, 2015);
        var second = new PropertyRegistrationRequestDetails(PropertyType.Unit, CompleteAddress(), 120m, 3, 2, 2015);

        Assert.NotSame(first, second);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Details_DifferInAnyMember_AreNotEqual()
    {
        Assert.NotEqual(CompleteUnit(), CompleteUnit(bedrooms: 4));
        Assert.NotEqual(
            CompleteUnit(),
            new PropertyRegistrationRequestDetails(
                PropertyType.Unit,
                new PropertyAddress("SA", city: "Jeddah", district: "Hittin", addressLine: "Villa 12, King Fahd Rd"),
                120m,
                3,
                2,
                2015));
    }
}
