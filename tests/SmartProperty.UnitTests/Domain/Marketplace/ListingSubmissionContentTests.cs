using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class ListingSubmissionContentTests
{
    public static IEnumerable<object?[]> MissingTextCases()
    {
        for (var field = 0; field < 7; field++)
            foreach (var value in new string?[] { null, "", " \t" })
                yield return new object?[] { field, value };
    }

    [Theory]
    [MemberData(nameof(MissingTextCases))]
    public void EveryRequiredTextFieldRejectsMissingValues(int field, string? value)
    {
        var text = new[] { "Country", "Region", "City", "District", "Title", "Description", "USD" };
        text[field] = value!;
        Assert.ThrowsAny<ArgumentException>(() => new ListingSubmissionContent(text[0], text[1], text[2], text[3], text[4], text[5],
            PropertyType.Unit, ListingPurpose.Sale, 100m, text[6], null));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 1)]
    [InlineData(3, 0)]
    [InlineData(3, 99)]
    public void UndefinedTypeOrPurposeFails(int type, int purpose) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        new ListingSubmissionContent("C", "R", "C", "D", "T", "D", (PropertyType)type, (ListingPurpose)purpose, 10m, "USD", null));

    [Fact]
    public void ContentPreservesValuesWithoutNormalizationOrRounding()
    {
        var content = new ListingSubmissionContent(" Country ", "Region", "City", "District", "Title", "Description",
            PropertyType.Unit, ListingPurpose.Rent, 123.4567m, "Currency", RentPeriod.Yearly);
        Assert.Equal(" Country ", content.Country);
        Assert.Equal("Region", content.Region);
        Assert.Equal("City", content.City);
        Assert.Equal("District", content.District);
        Assert.Equal("Title", content.Title);
        Assert.Equal("Description", content.Description);
        Assert.Equal(123.4567m, content.Amount);
        Assert.Equal("Currency", content.Currency);
        Assert.All(typeof(ListingSubmissionContent).GetProperties(), property => Assert.Null(property.SetMethod));
    }
}
