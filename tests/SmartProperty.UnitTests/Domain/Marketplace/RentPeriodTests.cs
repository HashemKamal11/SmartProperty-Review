using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class RentPeriodTests
{
    [Fact]
    public void ExactlyTwoSupportedValues() => Assert.Equal(new[] { RentPeriod.Monthly, RentPeriod.Yearly }, Enum.GetValues<RentPeriod>());

    [Theory]
    [InlineData(RentPeriod.Monthly)]
    [InlineData(RentPeriod.Yearly)]
    public void RentPreservesExplicitPeriod(RentPeriod period) => Assert.Equal(period, Content(ListingPurpose.Rent, period).RentPeriod);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void MissingOrUnknownRentPeriodFails(int? period) => Assert.Throws<ArgumentException>(() => Content(ListingPurpose.Rent, (RentPeriod?)period));

    [Theory]
    [InlineData(RentPeriod.Monthly)]
    [InlineData(RentPeriod.Yearly)]
    [InlineData((RentPeriod)0)]
    public void SaleRejectsAnyPeriod(RentPeriod period) => Assert.Throws<ArgumentException>(() => Content(ListingPurpose.Sale, period));

    [Fact]
    public void SaleHasNoPeriod() => Assert.Null(Content(ListingPurpose.Sale, null).RentPeriod);

    private static ListingSubmissionContent Content(ListingPurpose purpose, RentPeriod? period) =>
        new("Country", "Region", "City", "District", "Title", "Description", PropertyType.Unit, purpose, 100m, "USD", period);
}
