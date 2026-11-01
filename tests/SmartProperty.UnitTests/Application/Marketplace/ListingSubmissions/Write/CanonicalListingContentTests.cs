using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// Draft content takes its location and type from the registry, and a stored draft is compared with the registry as
/// it is now.
/// </summary>
public sealed class CanonicalListingContentTests
{
    private static readonly Guid PropertyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void ASaleTakesItsLocationAndTypeFromTheRegistryAndItsClaimsFromTheInput()
    {
        var result = CanonicalListingContent.Build(Property(), ListingPurpose.Sale, Input());

        Assert.True(result.IsSuccess);
        var content = result.Value;
        Assert.Equal("SA", content.Country);
        Assert.Equal("Riyadh Province", content.Region);
        Assert.Equal("Riyadh", content.City);
        Assert.Equal("Al Olaya", content.District);
        Assert.Equal(PropertyType.Unit, content.PropertyType);
        Assert.Equal("Two bedroom apartment", content.Title);
        Assert.Equal("Close to the centre.", content.Description);
        Assert.Equal(1250000m, content.Amount);
        Assert.Equal("SAR", content.Currency);
        Assert.Equal(ListingPurpose.Sale, content.Purpose);
        Assert.Null(content.RentPeriod);
    }

    [Theory]
    [InlineData(RentPeriod.Monthly)]
    [InlineData(RentPeriod.Yearly)]
    public void ARentalCarriesItsRentPeriod(RentPeriod period)
    {
        var result = CanonicalListingContent.Build(Property(), ListingPurpose.Rent, Input(period));

        Assert.True(result.IsSuccess);
        Assert.Equal(period, result.Value.RentPeriod);
    }

    [Fact]
    public void TheRegistryTypeIsUsedWhateverTheCallerMightHaveWanted()
    {
        var land = CanonicalListingContent.Build(Property() with { PropertyType = PropertyType.Land }, ListingPurpose.Sale, Input());

        Assert.Equal(PropertyType.Land, land.Value.PropertyType);
    }

    [Theory]
    [InlineData(null, "Riyadh", "Al Olaya")]
    [InlineData("Riyadh Province", null, "Al Olaya")]
    [InlineData("Riyadh Province", "Riyadh", null)]
    [InlineData(" ", "Riyadh", "Al Olaya")]
    [InlineData("Riyadh Province", "", "Al Olaya")]
    [InlineData("Riyadh Province", "Riyadh", "  ")]
    public void APropertyWithoutAFullLocationCannotBecomeAListingAndNothingIsInvented(string? region, string? city, string? district)
    {
        var property = Property() with { Region = region, City = city, District = district };

        var result = CanonicalListingContent.Build(property, ListingPurpose.Sale, Input());

        Assert.True(result.IsFailure);
        Assert.Same(WriteErrors.PropertyUnavailable, result.Error);
    }

    [Fact]
    public void ABlankCountryCannotBecomeAListing()
    {
        var result = CanonicalListingContent.Build(Property() with { CountryCode = " " }, ListingPurpose.Sale, Input());

        Assert.Same(WriteErrors.PropertyUnavailable, result.Error);
    }

    [Fact]
    public void ContentTheDomainRefusesIsAGenericValidationFailureNotAnException()
    {
        var rentalWithoutAPeriod = CanonicalListingContent.Build(Property(), ListingPurpose.Rent, Input(rentPeriod: null));
        var saleWithAPeriod = CanonicalListingContent.Build(Property(), ListingPurpose.Sale, Input(RentPeriod.Monthly));

        foreach (var result in new[] { rentalWithoutAPeriod, saleWithAPeriod })
        {
            Assert.True(result.IsFailure);
            Assert.Equal("validation.failed", result.Error!.Code);
            Assert.DoesNotContain("rent", result.Error.Description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void StoredContentThatMatchesTheRegistryMatches()
    {
        var stored = CanonicalListingContent.Build(Property(), ListingPurpose.Sale, Input()).Value;

        Assert.True(CanonicalListingContent.Matches(stored, Property()));
    }

    [Fact]
    public void AnyChangeToTheRegistryLocationOrTypeSinceTheDraftWasWrittenIsDetected()
    {
        var stored = CanonicalListingContent.Build(Property(), ListingPurpose.Sale, Input()).Value;

        Assert.False(CanonicalListingContent.Matches(stored, Property() with { CountryCode = "AE" }));
        Assert.False(CanonicalListingContent.Matches(stored, Property() with { Region = "Makkah Province" }));
        Assert.False(CanonicalListingContent.Matches(stored, Property() with { City = "Jeddah" }));
        Assert.False(CanonicalListingContent.Matches(stored, Property() with { District = "Al Malaz" }));
        Assert.False(CanonicalListingContent.Matches(stored, Property() with { PropertyType = PropertyType.Building }));
    }

    [Fact]
    public void ARegistryThatNoLongerHasAFullLocationNeverMatches()
    {
        var stored = CanonicalListingContent.Build(Property(), ListingPurpose.Sale, Input()).Value;

        Assert.False(CanonicalListingContent.Matches(stored, Property() with { District = null }));
        Assert.False(CanonicalListingContent.Matches(stored, Property() with { Region = " " }));
    }

    [Fact]
    public void ComparisonIsExactIncludingCase()
    {
        var stored = CanonicalListingContent.Build(Property(), ListingPurpose.Sale, Input()).Value;

        Assert.False(CanonicalListingContent.Matches(stored, Property() with { City = "riyadh" }));
    }

    [Fact]
    public void NullArgumentsAreProgrammingErrors()
    {
        Assert.Throws<ArgumentNullException>(() => CanonicalListingContent.Build(null!, ListingPurpose.Sale, Input()));
        Assert.Throws<ArgumentNullException>(() => CanonicalListingContent.Build(Property(), ListingPurpose.Sale, null!));
        Assert.Throws<ArgumentNullException>(() => CanonicalListingContent.Matches(null!, Property()));
        Assert.Throws<ArgumentNullException>(
            () => CanonicalListingContent.Matches(CanonicalListingContent.Build(Property(), ListingPurpose.Sale, Input()).Value, null!));
    }

    private static LockedPropertyView Property()
    {
        return new LockedPropertyView(PropertyId, true, false, PropertyType.Unit, "SA", "Riyadh Province", "Riyadh", "Al Olaya");
    }

    private static ListingDraftContentInput Input(RentPeriod? rentPeriod = null)
    {
        return new ListingDraftContentInput("Two bedroom apartment", "Close to the centre.", 1250000m, "SAR", rentPeriod, []);
    }
}
