using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.PropertyRegistry;

/// <summary>
/// Covers the address invariants. Only the country code is required; the city is optional because land may
/// exist outside a clear municipal boundary.
/// </summary>
public sealed class PropertyAddressTests
{
    [Fact]
    public void Constructor_AcceptsACountryCodeAlone()
    {
        var address = new PropertyAddress("SA");

        Assert.Equal("SA", address.CountryCode);
        Assert.Null(address.City);
        Assert.Null(address.Region);
        Assert.Null(address.District);
        Assert.Null(address.AddressLine);
        Assert.Null(address.PostalCode);
        Assert.Null(address.Latitude);
        Assert.Null(address.Longitude);
    }

    [Fact]
    public void Constructor_TrimsTheCountryCode()
    {
        var address = new PropertyAddress("  SA  ");

        Assert.Equal("SA", address.CountryCode);
    }

    [Fact]
    public void Constructor_UpperCasesTheCountryCode()
    {
        // ISO 3166-1 alpha-2 codes are canonically upper case, so stored values stay directly comparable.
        var address = new PropertyAddress("sa");

        Assert.Equal("SA", address.CountryCode);
    }

    [Fact]
    public void Constructor_RejectsANullCountryCode()
    {
        Assert.Throws<ArgumentException>(() => new PropertyAddress(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Constructor_RejectsABlankCountryCode(string countryCode)
    {
        Assert.Throws<ArgumentException>(() => new PropertyAddress(countryCode));
    }

    [Theory]
    [InlineData("S")]
    [InlineData("SAU")]
    [InlineData("S1")]
    [InlineData("12")]
    [InlineData("S-")]
    [InlineData("سا")]
    public void Constructor_RejectsACountryCodeThatIsNotIsoAlpha2(string countryCode)
    {
        Assert.Throws<ArgumentException>(() => new PropertyAddress(countryCode));
    }

    [Fact]
    public void Constructor_TrimsTheOptionalTextValues()
    {
        var address = new PropertyAddress(
            "SA",
            city: "  Riyadh  ",
            region: "  Riyadh Province  ",
            district: "  Al Olaya  ",
            addressLine: "  King Fahd Road  ",
            postalCode: "  12214  ");

        Assert.Equal("Riyadh", address.City);
        Assert.Equal("Riyadh Province", address.Region);
        Assert.Equal("Al Olaya", address.District);
        Assert.Equal("King Fahd Road", address.AddressLine);
        Assert.Equal("12214", address.PostalCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_TreatsBlankOptionalTextAsAbsent(string? value)
    {
        var address = new PropertyAddress(
            "SA",
            city: value,
            region: value,
            district: value,
            addressLine: value,
            postalCode: value);

        Assert.Null(address.City);
        Assert.Null(address.Region);
        Assert.Null(address.District);
        Assert.Null(address.AddressLine);
        Assert.Null(address.PostalCode);
    }

    [Fact]
    public void Constructor_KeepsCoordinatesWithinRange()
    {
        var address = new PropertyAddress("SA", latitude: 24.7136m, longitude: 46.6753m);

        Assert.Equal(24.7136m, address.Latitude);
        Assert.Equal(46.6753m, address.Longitude);
    }

    [Fact]
    public void Constructor_AcceptsTheLatitudeBounds()
    {
        Assert.Equal(-90m, new PropertyAddress("SA", latitude: -90m).Latitude);
        Assert.Equal(0m, new PropertyAddress("SA", latitude: 0m).Latitude);
        Assert.Equal(90m, new PropertyAddress("SA", latitude: 90m).Latitude);
    }

    [Fact]
    public void Constructor_AcceptsTheLongitudeBounds()
    {
        Assert.Equal(-180m, new PropertyAddress("SA", longitude: -180m).Longitude);
        Assert.Equal(0m, new PropertyAddress("SA", longitude: 0m).Longitude);
        Assert.Equal(180m, new PropertyAddress("SA", longitude: 180m).Longitude);
    }

    [Fact]
    public void Constructor_RejectsALatitudeBelowRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyAddress("SA", latitude: -90.0001m));
    }

    [Fact]
    public void Constructor_RejectsALatitudeAboveRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyAddress("SA", latitude: 90.0001m));
    }

    [Fact]
    public void Constructor_RejectsALongitudeBelowRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyAddress("SA", longitude: -180.0001m));
    }

    [Fact]
    public void Constructor_RejectsALongitudeAboveRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PropertyAddress("SA", longitude: 180.0001m));
    }

    [Fact]
    public void PropertyAddress_ComparesByValue()
    {
        // Normalization happens in the constructor, so equivalent input yields an equal address.
        var first = new PropertyAddress("SA", city: "Riyadh");
        var second = new PropertyAddress("sa", city: "  Riyadh  ");

        Assert.Equal(first, second);
    }
}
