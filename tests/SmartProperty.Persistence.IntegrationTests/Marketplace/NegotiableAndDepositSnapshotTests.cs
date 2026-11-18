using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.Context;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

/// <summary>
/// A sale's negotiable flag and a rent option's security deposit are additive snapshot members: written only when
/// stated, so every snapshot stored before them keeps exactly its shape and still reads.
/// </summary>
public sealed class NegotiableAndDepositSnapshotTests
{
    private static ValueConverter Converter()
    {
        using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql().Options);

        return context.Model.FindEntityType(typeof(ListingSubmission))!.FindProperty("Content")!.GetTypeMapping().Converter!;
    }

    private static ListingSubmissionContent Sale(bool? negotiable) =>
        new("EG", "Cairo Governorate", "Cairo", "Maadi", "Villa", "A villa", PropertyType.Villa, ListingPurpose.Sale,
            5_000_000m, "EGP", null, negotiable: negotiable);

    private static ListingSubmissionContent Rent(decimal? monthlyDeposit) =>
        ListingSubmissionContent.ForRent("EG", "Cairo Governorate", "Cairo", "Maadi", "Flat", "A flat", PropertyType.Apartment,
            [new RentPricingOption(RentPeriod.Monthly, 20_000m, "EGP", monthlyDeposit), new RentPricingOption(RentPeriod.Daily, 1_000m, "EGP")],
            new RentalTerms("Africa/Cairo", new TimeOnly(14, 0), new TimeOnly(11, 0),
                [new RentalWindow(new DateOnly(2027, 6, 1), new DateOnly(2027, 9, 1))]));

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.GetProperty("Payload").Clone();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStatedNegotiableFlagIsWrittenAndReadBack(bool negotiable)
    {
        var converter = Converter();
        var json = Assert.IsType<string>(converter.ConvertToProvider(Sale(negotiable)));

        Assert.Equal(negotiable, Payload(json).GetProperty("Negotiable").GetBoolean());
        Assert.Equal(negotiable, Assert.IsType<ListingSubmissionContent>(converter.ConvertFromProvider(json)).Negotiable);
    }

    [Fact]
    public void ASaleThatDoesNotStateItKeepsItsOldShape()
    {
        var converter = Converter();
        var json = Assert.IsType<string>(converter.ConvertToProvider(Sale(negotiable: null)));

        Assert.False(Payload(json).TryGetProperty("Negotiable", out _));
        Assert.Null(Assert.IsType<ListingSubmissionContent>(converter.ConvertFromProvider(json)).Negotiable);
    }

    [Fact]
    public void ASalesAvailabilityDateIsWrittenAsADateAndReadBack()
    {
        var converter = Converter();
        var sale = new ListingSubmissionContent("EG", "Cairo Governorate", "Cairo", "Maadi", "Villa", "A villa", PropertyType.Villa,
            ListingPurpose.Sale, 5_000_000m, "EGP", null, negotiable: false, availableFrom: new DateOnly(2027, 3, 15));
        var json = Assert.IsType<string>(converter.ConvertToProvider(sale));

        Assert.Equal("2027-03-15", Payload(json).GetProperty("AvailableFrom").GetString());
        var restored = Assert.IsType<ListingSubmissionContent>(converter.ConvertFromProvider(json));
        Assert.Equal(new DateOnly(2027, 3, 15), restored.AvailableFrom);
        Assert.False(restored.Negotiable);
    }

    [Fact]
    public void APlainSaleSnapshotIsExactlyTheShapeItAlwaysHad()
    {
        var json = Assert.IsType<string>(Converter().ConvertToProvider(Sale(negotiable: null)));

        Assert.Equal(
            """{"FormatVersion":1,"Payload":{"Country":"EG","Region":"Cairo Governorate","City":"Cairo","District":"Maadi","Title":"Villa","Description":"A villa","PropertyType":4,"Purpose":1,"Amount":5000000,"Currency":"EGP","RentPeriod":null}}""",
            json);
    }

    [Fact]
    public void ADepositIsWrittenOnlyOnTheOptionThatStatesIt()
    {
        var converter = Converter();
        var json = Assert.IsType<string>(converter.ConvertToProvider(Rent(monthlyDeposit: 40_000m)));
        var options = Payload(json).GetProperty("PricingOptions").EnumerateArray().ToArray();

        Assert.Equal(40_000m, options.Single(o => o.GetProperty("Period").GetInt32() == (int)RentPeriod.Monthly).GetProperty("Deposit").GetDecimal());
        Assert.False(options.Single(o => o.GetProperty("Period").GetInt32() == (int)RentPeriod.Daily).TryGetProperty("Deposit", out _));

        var restored = Assert.IsType<ListingSubmissionContent>(converter.ConvertFromProvider(json));
        Assert.Equal(40_000m, restored.PricingOptions.Single(o => o.Period == RentPeriod.Monthly).Deposit);
        Assert.Null(restored.PricingOptions.Single(o => o.Period == RentPeriod.Daily).Deposit);
    }

    [Fact]
    public void ASnapshotStoredBeforeTheseMembersStillReads()
    {
        const string old = """
            {"FormatVersion":1,"Payload":{"Country":"EG","Region":"Cairo Governorate","City":"Cairo","District":"Maadi",
            "Title":"Flat","Description":"A flat","PropertyType":3,"Purpose":2,"Amount":20000,"Currency":"EGP","RentPeriod":3,
            "PricingOptions":[{"Period":3,"Amount":20000,"Currency":"EGP"}],
            "RentalTerms":{"TimeZoneId":"Africa/Cairo","CheckInTime":"14:00:00","CheckOutTime":"11:00:00","MinimumStayDays":null,
            "Windows":[{"StartDate":"2027-06-01","EndDate":"2027-09-01"}]}}}
            """;

        var restored = Assert.IsType<ListingSubmissionContent>(Converter().ConvertFromProvider(old));

        Assert.Null(restored.Negotiable);
        Assert.Null(Assert.Single(restored.PricingOptions).Deposit);
    }

    [Fact]
    public void TheDomainRefusesANegativeDepositAndANegotiableRental()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RentPricingOption(RentPeriod.Monthly, 20_000m, "EGP", -1m));
        Assert.Throws<ArgumentException>(() => new ListingSubmissionContent(
            "EG", "Cairo Governorate", "Cairo", "Maadi", "Flat", "A flat", PropertyType.Apartment, ListingPurpose.Rent,
            20_000m, "EGP", RentPeriod.Monthly, negotiable: true));
    }
}
