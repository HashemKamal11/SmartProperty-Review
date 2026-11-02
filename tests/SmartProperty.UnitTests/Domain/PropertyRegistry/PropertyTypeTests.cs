using SmartProperty.Application.PropertyRegistry.Ai;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Domain.PropertyRegistry;

/// <summary>
/// Pins the property types to the AI search service's closed list (<c>GET /v1/options</c>) and the numbers the
/// marketplace has persisted.
/// </summary>
public sealed class PropertyTypeTests
{
    [Fact]
    public void PropertyType_DefinesExactlyTheSixteenAiTypes()
    {
        Assert.Equal(
            [
                "Land", "Building", "Apartment", "Villa", "Duplex", "Studio", "Chalet", "Townhouse", "TwinHouse",
                "Penthouse", "StandaloneVilla", "HotelApartment", "Office", "Shop", "Warehouse", "Farm"
            ],
            Enum.GetNames<PropertyType>());
    }

    [Theory]
    [InlineData(PropertyType.Land, 1)]
    [InlineData(PropertyType.Building, 2)]
    [InlineData(PropertyType.Apartment, 3)]
    public void PropertyType_KeepsTheNumbersTheMarketplaceHasPersisted(PropertyType type, int number)
    {
        Assert.Equal(number, (int)type);
    }

    [Theory]
    [InlineData(PropertyType.Land, "Land")]
    [InlineData(PropertyType.Building, "Building")]
    [InlineData(PropertyType.Apartment, "Apartment")]
    [InlineData(PropertyType.Villa, "Villa")]
    [InlineData(PropertyType.Duplex, "Duplex")]
    [InlineData(PropertyType.Studio, "Studio")]
    [InlineData(PropertyType.Chalet, "Chalet")]
    [InlineData(PropertyType.Townhouse, "Townhouse")]
    [InlineData(PropertyType.TwinHouse, "Twin House")]
    [InlineData(PropertyType.Penthouse, "Penthouse")]
    [InlineData(PropertyType.StandaloneVilla, "Standalone Villa")]
    [InlineData(PropertyType.HotelApartment, "Hotel Apartment")]
    [InlineData(PropertyType.Office, "Office")]
    [InlineData(PropertyType.Shop, "Shop")]
    [InlineData(PropertyType.Warehouse, "Warehouse")]
    [InlineData(PropertyType.Farm, "Farm")]
    public void AiVocabulary_SpellsEveryTypeAsTheAiServiceDoes(PropertyType type, string expected)
    {
        Assert.Equal(expected, AiVocabulary.ToAi(type));
    }

    [Fact]
    public void AiVocabulary_RejectsAnUndefinedType()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AiVocabulary.ToAi((PropertyType)99));
    }

    [Theory]
    [InlineData(FinishingStatus.Finished, "Finished")]
    [InlineData(FinishingStatus.LuxuryFinished, "Luxury Finished")]
    [InlineData(FinishingStatus.SemiFinished, "Semi Finished")]
    [InlineData(FinishingStatus.CoreAndShell, "Core & Shell")]
    public void AiVocabulary_SpellsEveryFinishingStatusAsTheAiServiceDoes(FinishingStatus status, string expected)
    {
        Assert.Equal(expected, AiVocabulary.ToAi(status));
    }

    [Theory]
    [InlineData(TransactionType.Sale, "Sale")]
    [InlineData(TransactionType.Rent, "Rent")]
    public void AiVocabulary_SpellsEveryTransactionTypeAsTheAiServiceDoes(TransactionType type, string expected)
    {
        Assert.Equal(expected, AiVocabulary.ToAi(type));
    }

    [Theory]
    [InlineData(ListingStatus.Available, "Available")]
    [InlineData(ListingStatus.Reserved, "Reserved")]
    [InlineData(ListingStatus.Sold, "Sold")]
    [InlineData(ListingStatus.Rented, "Rented")]
    public void AiVocabulary_SpellsEveryListingStatusAsTheAiServiceDoes(ListingStatus status, string expected)
    {
        Assert.Equal(expected, AiVocabulary.ToAi(status));
    }
}
