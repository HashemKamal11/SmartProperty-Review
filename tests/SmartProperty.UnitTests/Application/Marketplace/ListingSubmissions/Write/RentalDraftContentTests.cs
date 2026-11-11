using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// A rental is stated with pricing options and terms; its time zone is the property's own and never a request value.
/// </summary>
public sealed class RentalDraftContentTests
{
    private static readonly RentalWindowInput Summer = new(new DateOnly(2027, 6, 1), new DateOnly(2027, 9, 1));

    private static LockedPropertyView Property(string? timeZoneId = "Africa/Cairo") =>
        new(Guid.NewGuid(), true, false, PropertyType.Apartment, "EG", "Cairo Governorate", "Cairo", "Maadi", timeZoneId);

    private static RentPricingOptionInput[] FourOptions() =>
    [
        new(RentPeriod.Daily, 1_000m, "EGP"),
        new(RentPeriod.Weekly, 6_000m, "EGP"),
        new(RentPeriod.Monthly, 20_000m, "EGP"),
        new(RentPeriod.Yearly, 200_000m, "EGP"),
    ];

    private static ListingDraftContentInput Rental(
        IReadOnlyList<RentPricingOptionInput>? options = null,
        RentalTermsInput? terms = null,
        RentPeriod? singlePeriod = null) =>
        new("Flat", "A flat", 0m, string.Empty, singlePeriod, [],
            options ?? FourOptions(),
            terms ?? new RentalTermsInput(new TimeOnly(14, 0), new TimeOnly(11, 0), [Summer]));

    // ---- validation

    [Fact]
    public void ARentalWithEveryPeriodAndItsTermsIsValid()
    {
        Assert.Null(ListingDraftContentValidator.Validate(Rental(), ListingPurpose.Rent));
    }

    [Fact]
    public void ARentalStatedWithOneSinglePriceStillValidatesAsBefore()
    {
        var legacy = new ListingDraftContentInput("Flat", "A flat", 5_000m, "EGP", RentPeriod.Monthly, []);

        Assert.Null(ListingDraftContentValidator.Validate(legacy, ListingPurpose.Rent));
    }

    [Fact]
    public void OptionsNeverBelongToASale()
    {
        var error = ListingDraftContentValidator.Validate(Rental(), ListingPurpose.Sale);

        Assert.Contains("sale", error!.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenThePurposeIsNotYetKnownTheOptionsAreAccepted()
    {
        Assert.Null(ListingDraftContentValidator.Validate(Rental(), purpose: null));
    }

    [Fact]
    public void ARentalWithOptionsMustNotAlsoStateASinglePeriod()
    {
        Assert.NotNull(ListingDraftContentValidator.Validate(Rental(singlePeriod: RentPeriod.Monthly), ListingPurpose.Rent));
    }

    [Fact]
    public void TermsWithoutOptionsAreRefused()
    {
        var input = new ListingDraftContentInput("Flat", "A flat", 5_000m, "EGP", RentPeriod.Monthly, [],
            RentalTerms: new RentalTermsInput(new TimeOnly(14, 0), new TimeOnly(11, 0), [Summer]));

        Assert.NotNull(ListingDraftContentValidator.Validate(input, ListingPurpose.Rent));
    }

    [Fact]
    public void OptionsWithoutTermsAreRefused()
    {
        var input = new ListingDraftContentInput("Flat", "A flat", 0m, string.Empty, null, [], FourOptions(), null);

        Assert.Contains("terms", ListingDraftContentValidator.Validate(input, ListingPurpose.Rent)!.Description);
    }

    [Fact]
    public void EachPeriodMayBeOfferedOnlyOnce()
    {
        var options = new RentPricingOptionInput[] { new(RentPeriod.Daily, 10m, "EGP"), new(RentPeriod.Daily, 20m, "EGP") };

        Assert.Contains("once", ListingDraftContentValidator.Validate(Rental(options), ListingPurpose.Rent)!.Description);
    }

    [Fact]
    public void ThereAreAtMostFourOptions()
    {
        var options = FourOptions().Append(new RentPricingOptionInput(RentPeriod.Daily, 5m, "EGP")).ToArray();

        Assert.NotNull(ListingDraftContentValidator.Validate(Rental(options), ListingPurpose.Rent));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void EveryOptionNeedsAnAmountAboveZero(int amount)
    {
        var options = new RentPricingOptionInput[] { new(RentPeriod.Monthly, amount, "EGP") };

        Assert.Contains("amount", ListingDraftContentValidator.Validate(Rental(options), ListingPurpose.Rent)!.Description);
    }

    [Theory]
    [InlineData("egp")]
    [InlineData("EG")]
    [InlineData("")]
    [InlineData("EGPT")]
    public void EveryOptionNeedsAThreeLetterUpperCaseCurrency(string currency)
    {
        var options = new RentPricingOptionInput[] { new(RentPeriod.Monthly, 100m, currency) };

        Assert.Contains("currency", ListingDraftContentValidator.Validate(Rental(options), ListingPurpose.Rent)!.Description);
    }

    [Fact]
    public void AnUndefinedPeriodIsRefused()
    {
        var options = new RentPricingOptionInput[] { new((RentPeriod)42, 100m, "EGP") };

        Assert.NotNull(ListingDraftContentValidator.Validate(Rental(options), ListingPurpose.Rent));
    }

    [Fact]
    public void WindowsMustExistEndAfterTheyStartAndNeverOverlap()
    {
        RentalTermsInput Terms(params RentalWindowInput[] windows) => new(new TimeOnly(14, 0), new TimeOnly(11, 0), windows);

        Assert.NotNull(ListingDraftContentValidator.Validate(Rental(terms: Terms()), ListingPurpose.Rent));
        Assert.NotNull(ListingDraftContentValidator.Validate(
            Rental(terms: Terms(new RentalWindowInput(new DateOnly(2027, 6, 5), new DateOnly(2027, 6, 5)))), ListingPurpose.Rent));
        Assert.NotNull(ListingDraftContentValidator.Validate(
            Rental(terms: Terms(Summer, new RentalWindowInput(new DateOnly(2027, 8, 1), new DateOnly(2027, 10, 1)))), ListingPurpose.Rent));
        Assert.Null(ListingDraftContentValidator.Validate(
            Rental(terms: Terms(Summer, new RentalWindowInput(new DateOnly(2027, 9, 1), new DateOnly(2027, 10, 1)))), ListingPurpose.Rent));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AMinimumStayBelowOneDayIsRefused(int days)
    {
        var terms = new RentalTermsInput(new TimeOnly(14, 0), new TimeOnly(11, 0), [Summer], days);

        Assert.NotNull(ListingDraftContentValidator.Validate(Rental(terms: terms), ListingPurpose.Rent));
    }

    // ---- building the content

    [Fact]
    public void TheRentalIsBuiltWithEveryOptionAndThePropertysOwnTimeZone()
    {
        var result = CanonicalListingContent.Build(Property("Africa/Cairo"), ListingPurpose.Rent, Rental());

        Assert.True(result.IsSuccess, result.Error?.Code);
        var content = result.Value;
        Assert.Equal(4, content.PricingOptions.Count);
        Assert.Equal(20_000m, content.Amount);
        Assert.Equal(RentPeriod.Monthly, content.RentPeriod);
        Assert.Equal("Africa/Cairo", content.RentalTerms!.TimeZoneId);
        Assert.Equal(new TimeOnly(14, 0), content.RentalTerms.CheckInTime);
        Assert.Equal([new RentalWindow(new DateOnly(2027, 6, 1), new DateOnly(2027, 9, 1))], content.RentalTerms.Windows);
    }

    [Fact]
    public void AnotherCountrysTimeZoneIsUsedForAnotherProperty()
    {
        var result = CanonicalListingContent.Build(Property("Asia/Riyadh"), ListingPurpose.Rent, Rental());

        Assert.Equal("Asia/Riyadh", result.Value.RentalTerms!.TimeZoneId);
    }

    [Fact]
    public void ARentalIsRefusedClearlyWhenThePropertyHasNoTimeZone()
    {
        var result = CanonicalListingContent.Build(Property(timeZoneId: null), ListingPurpose.Rent, Rental());

        Assert.Same(WriteErrors.RentalTimeZoneUnavailable, result.Error);
        Assert.Equal("listing_submissions.rental_time_zone_unavailable", result.Error!.Code);
    }

    [Fact]
    public void ASaleNeedsNoTimeZone()
    {
        var sale = new ListingDraftContentInput("House", "A house", 5_000_000m, "EGP", null, []);

        var result = CanonicalListingContent.Build(Property(timeZoneId: null), ListingPurpose.Sale, sale);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.PricingOptions);
    }
}
