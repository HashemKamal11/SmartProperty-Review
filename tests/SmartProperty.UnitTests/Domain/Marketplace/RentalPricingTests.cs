using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class RentalPricingTests
{
    private const string Cairo = "Africa/Cairo";

    private static readonly RentalWindow Summer = new(new DateOnly(2027, 6, 1), new DateOnly(2027, 9, 1));

    private static RentalTerms Terms(params RentalWindow[] windows) =>
        new(Cairo, new TimeOnly(14, 0), new TimeOnly(11, 0), windows.Length == 0 ? [Summer] : windows);

    private static RentPricingOption[] AllFour() =>
    [
        new(RentPeriod.Daily, 1_000m, "EGP"),
        new(RentPeriod.Weekly, 6_000m, "EGP"),
        new(RentPeriod.Monthly, 20_000m, "EGP"),
        new(RentPeriod.Yearly, 200_000m, "EGP"),
    ];

    // ---- one pricing option

    [Fact]
    public void AnOptionKeepsItsOwnPeriodAmountAndCurrency()
    {
        var option = new RentPricingOption(RentPeriod.Weekly, 6_000m, "EGP");

        Assert.Equal(RentPeriod.Weekly, option.Period);
        Assert.Equal(6_000m, option.Amount);
        Assert.Equal("EGP", option.Currency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnOptionNeedsAnAmountAboveZero(int amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RentPricingOption(RentPeriod.Daily, amount, "EGP"));
    }

    [Theory]
    [InlineData("egp")]
    [InlineData("EG")]
    [InlineData("EGPX")]
    [InlineData("E1P")]
    [InlineData("")]
    [InlineData(null)]
    public void AnOptionNeedsAThreeLetterUpperCaseCurrency(string? currency)
    {
        Assert.Throws<ArgumentException>(() => new RentPricingOption(RentPeriod.Daily, 10m, currency!));
    }

    [Fact]
    public void AnOptionNeedsADefinedPeriod()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RentPricingOption((RentPeriod)99, 10m, "EGP"));
    }

    [Fact]
    public void OptionsAreComparedByValue()
    {
        Assert.Equal(new RentPricingOption(RentPeriod.Daily, 10m, "EGP"), new RentPricingOption(RentPeriod.Daily, 10m, "EGP"));
        Assert.NotEqual(new RentPricingOption(RentPeriod.Daily, 10m, "EGP"), new RentPricingOption(RentPeriod.Daily, 10m, "USD"));
    }

    [Fact]
    public void ThePrimaryOptionIsMonthlyThenYearlyThenWeeklyThenDaily()
    {
        var all = AllFour();

        Assert.Equal(RentPeriod.Monthly, RentPricingOption.PrimaryOf(all).Period);
        Assert.Equal(RentPeriod.Yearly, RentPricingOption.PrimaryOf(all.Where(o => o.Period != RentPeriod.Monthly).ToArray()).Period);
        Assert.Equal(RentPeriod.Weekly, RentPricingOption.PrimaryOf(all.Where(o => o.Period is RentPeriod.Weekly or RentPeriod.Daily).ToArray()).Period);
        Assert.Equal(RentPeriod.Daily, RentPricingOption.PrimaryOf([all[0]]).Period);
        Assert.Throws<ArgumentException>(() => RentPricingOption.PrimaryOf([]));
    }

    // ---- windows and terms

    [Fact]
    public void AWindowMustEndAfterItStartsAndCoversOnlyWhatLiesInsideIt()
    {
        Assert.Throws<ArgumentException>(() => new RentalWindow(new DateOnly(2027, 6, 1), new DateOnly(2027, 6, 1)));
        Assert.Throws<ArgumentException>(() => new RentalWindow(new DateOnly(2027, 6, 2), new DateOnly(2027, 6, 1)));
        Assert.True(Summer.Covers(new DateOnly(2027, 6, 1), new DateOnly(2027, 9, 1)));
        Assert.True(Summer.Covers(new DateOnly(2027, 7, 1), new DateOnly(2027, 7, 8)));
        Assert.False(Summer.Covers(new DateOnly(2027, 5, 31), new DateOnly(2027, 6, 5)));
        Assert.False(Summer.Covers(new DateOnly(2027, 8, 30), new DateOnly(2027, 9, 2)));
    }

    [Fact]
    public void TermsKeepTheirTimesZoneMinimumStayAndOrderedWindows()
    {
        var later = new RentalWindow(new DateOnly(2027, 12, 1), new DateOnly(2028, 1, 1));
        var terms = new RentalTerms(Cairo, new TimeOnly(14, 0), new TimeOnly(11, 0), [later, Summer], minimumStayDays: 3);

        Assert.Equal(Cairo, terms.TimeZoneId);
        Assert.Equal(new TimeOnly(14, 0), terms.CheckInTime);
        Assert.Equal(new TimeOnly(11, 0), terms.CheckOutTime);
        Assert.Equal(3, terms.MinimumStayDays);
        Assert.Equal([Summer, later], terms.Windows);
    }

    [Fact]
    public void TermsRefuseAnUnknownTimeZone()
    {
        Assert.Throws<ArgumentException>(() => new RentalTerms("Not/AZone", new TimeOnly(14, 0), new TimeOnly(11, 0), [Summer]));
        Assert.Throws<ArgumentException>(() => new RentalTerms(" ", new TimeOnly(14, 0), new TimeOnly(11, 0), [Summer]));
    }

    [Fact]
    public void TermsNeedAtLeastOneWindowAndNeverOverlappingOnes()
    {
        Assert.Throws<ArgumentException>(() => new RentalTerms(Cairo, new TimeOnly(14, 0), new TimeOnly(11, 0), []));
        Assert.Throws<ArgumentException>(() => Terms(Summer, new RentalWindow(new DateOnly(2027, 8, 1), new DateOnly(2027, 10, 1))));
    }

    [Fact]
    public void WindowsThatMeetEndToStartAreNotOverlapping()
    {
        var terms = Terms(Summer, new RentalWindow(new DateOnly(2027, 9, 1), new DateOnly(2027, 10, 1)));

        Assert.Equal(2, terms.Windows.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(RentalTerms.MaxMinimumStayDays + 1)]
    public void TheMinimumStayIsBounded(int days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RentalTerms(Cairo, new TimeOnly(14, 0), new TimeOnly(11, 0), [Summer], days));
    }

    [Fact]
    public void ThereAreAtMostFiftyWindows()
    {
        var windows = Enumerable.Range(0, RentalTerms.MaxWindows + 1)
            .Select(i => new RentalWindow(new DateOnly(2027, 1, 1).AddDays(i * 10), new DateOnly(2027, 1, 1).AddDays(i * 10 + 5)))
            .ToArray();

        Assert.Throws<ArgumentException>(() => new RentalTerms(Cairo, new TimeOnly(14, 0), new TimeOnly(11, 0), windows));
    }

    // ---- content

    [Fact]
    public void ARentalKeepsEveryOptionWithItsOwnPriceAndTheMonthlyOneIsThePlainPrice()
    {
        var content = ListingSubmissionContent.ForRent(
            "EG", "Cairo Governorate", "Cairo", "Maadi", "Flat", "A flat", PropertyType.Apartment, AllFour(), Terms());

        Assert.Equal(4, content.PricingOptions.Count);
        Assert.Equal(1_000m, content.PricingOptions.Single(o => o.Period == RentPeriod.Daily).Amount);
        Assert.Equal(6_000m, content.PricingOptions.Single(o => o.Period == RentPeriod.Weekly).Amount);
        Assert.Equal(20_000m, content.PricingOptions.Single(o => o.Period == RentPeriod.Monthly).Amount);
        Assert.Equal(200_000m, content.PricingOptions.Single(o => o.Period == RentPeriod.Yearly).Amount);
        Assert.Equal(RentPeriod.Monthly, content.RentPeriod);
        Assert.Equal(20_000m, content.Amount);
        Assert.Equal("EGP", content.Currency);
        Assert.Equal(ListingPurpose.Rent, content.Purpose);
        Assert.NotNull(content.RentalTerms);
    }

    [Fact]
    public void OptionsMayUseDifferentCurrencies()
    {
        RentPricingOption[] options = [new(RentPeriod.Daily, 50m, "USD"), new(RentPeriod.Monthly, 1_000m, "EGP")];

        var content = ListingSubmissionContent.ForRent(
            "EG", "R", "C", "D", "Flat", "A flat", PropertyType.Apartment, options, Terms());

        Assert.Equal("USD", content.PricingOptions.Single(o => o.Period == RentPeriod.Daily).Currency);
        Assert.Equal("EGP", content.PricingOptions.Single(o => o.Period == RentPeriod.Monthly).Currency);
        Assert.Equal("EGP", content.Currency);
    }

    [Fact]
    public void ARentalOffersEachPeriodAtMostOnce()
    {
        RentPricingOption[] options = [new(RentPeriod.Daily, 50m, "EGP"), new(RentPeriod.Daily, 60m, "EGP")];

        Assert.Throws<ArgumentException>(() => ListingSubmissionContent.ForRent(
            "EG", "R", "C", "D", "Flat", "A flat", PropertyType.Apartment, options, Terms()));
    }

    [Fact]
    public void OptionsAndTermsAreGivenTogether()
    {
        var option = new RentPricingOption(RentPeriod.Monthly, 100m, "EGP");

        Assert.Throws<ArgumentException>(() => new ListingSubmissionContent(
            "EG", "R", "C", "D", "Flat", "A flat", PropertyType.Apartment, ListingPurpose.Rent, 100m, "EGP",
            RentPeriod.Monthly, [option], rentalTerms: null));
        Assert.Throws<ArgumentException>(() => new ListingSubmissionContent(
            "EG", "R", "C", "D", "Flat", "A flat", PropertyType.Apartment, ListingPurpose.Rent, 100m, "EGP",
            RentPeriod.Monthly, pricingOptions: null, Terms()));
    }

    [Fact]
    public void ASaleCannotCarryRentalOptionsOrTerms()
    {
        var option = new RentPricingOption(RentPeriod.Monthly, 100m, "EGP");

        Assert.Throws<ArgumentException>(() => new ListingSubmissionContent(
            "EG", "R", "C", "D", "Flat", "A flat", PropertyType.Apartment, ListingPurpose.Sale, 100m, "EGP",
            null, [option], Terms()));
    }

    [Fact]
    public void ThePlainPriceCannotContradictThePrimaryOption()
    {
        var option = new RentPricingOption(RentPeriod.Monthly, 100m, "EGP");

        Assert.Throws<ArgumentException>(() => new ListingSubmissionContent(
            "EG", "R", "C", "D", "Flat", "A flat", PropertyType.Apartment, ListingPurpose.Rent, 999m, "EGP",
            RentPeriod.Monthly, [option], Terms()));
        Assert.Throws<ArgumentException>(() => new ListingSubmissionContent(
            "EG", "R", "C", "D", "Flat", "A flat", PropertyType.Apartment, ListingPurpose.Rent, 100m, "EGP",
            RentPeriod.Yearly, [option], Terms()));
        Assert.Throws<ArgumentException>(() => new ListingSubmissionContent(
            "EG", "R", "C", "D", "Flat", "A flat", PropertyType.Apartment, ListingPurpose.Rent, 100m, "USD",
            RentPeriod.Monthly, [option], Terms()));
    }

    [Fact]
    public void ARentalStoredWithASinglePriceHasNoOptionsOrTerms()
    {
        var legacy = new ListingSubmissionContent(
            "EG", "R", "C", "D", "Flat", "A flat", PropertyType.Apartment, ListingPurpose.Rent, 5_000m, "EGP", RentPeriod.Monthly);

        Assert.Empty(legacy.PricingOptions);
        Assert.Null(legacy.RentalTerms);
        Assert.Equal(5_000m, legacy.Amount);
    }

    [Fact]
    public void ASaleHasNoOptionsOrTerms()
    {
        var sale = new ListingSubmissionContent(
            "EG", "R", "C", "D", "Flat", "A flat", PropertyType.Apartment, ListingPurpose.Sale, 1_000_000m, "EGP", null);

        Assert.Empty(sale.PricingOptions);
        Assert.Null(sale.RentalTerms);
    }
}
