using System.Reflection;
using System.Text.Json;
using SmartProperty.Api.Contracts;
using SmartProperty.Api.Contracts.Marketplace.Public;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Public;

/// <summary>
/// What the public marketplace contract can and cannot say, asserted on the types and on their serialized form.
/// </summary>
/// <remarks>
/// Reflection and serialization only: no host, no database, no endpoint. The public DTOs are a closed vocabulary,
/// so every member must be a plain value or another public DTO; a Domain enum, an internal identifier or a private
/// field cannot be added without failing here and being reviewed.
/// </remarks>
public sealed class PublicListingContractPrivacyTests
{
    private static readonly Type[] PublicTypes =
    [
        typeof(PublicMoneyResponse),
        typeof(PublicLocationResponse),
        typeof(PublicListingSummaryResponse),
        typeof(PublicListingDetailResponse),
    ];

    // Name fragments that would indicate private, internal or privileged data. Matched case-insensitively against
    // every member name, constructor parameter name and serialized JSON property name.
    private static readonly string[] ForbiddenFragments =
    [
        "propertyid",
        "submission",
        "version",
        "draft",
        "approved",
        "latest",
        "evidence",
        "scan",
        "declar",
        "authority",
        "capacity",
        "owner",
        "legal",
        "review",
        "internal",
        "note",
        "workspace",
        "user",
        "submitter",
        "lister",
        "contact",
        "phone",
        "email",
        "permission",
        "role",
        "status",
        "addressline",
        "postalcode",
        "latitude",
        "longitude",
        "coordinates",
    ];

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static readonly Guid SampleListingId = Guid.Parse("a41e5c20-7b3d-4e0f-9a52-3d1f8e6b7c10");

    [Fact]
    public void NoMemberNameSuggestsPrivateData()
    {
        foreach (var type in PublicTypes)
        {
            var names = PropertiesOf(type).Select(property => property.Name)
                .Concat(type.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.Name ?? string.Empty));

            foreach (var name in names)
            {
                AssertNameIsNotForbidden(name, type.Name);
            }
        }
    }

    [Fact]
    public void EveryMemberIsAPlainValueOrAnotherPublicDto()
    {
        // A closed whitelist, so a Domain or Application type, an enum, a collection or a dictionary cannot be
        // introduced without failing here.
        foreach (var type in PublicTypes)
        {
            foreach (var property in PropertiesOf(type))
            {
                Assert.True(
                    IsAllowedMemberType(property.PropertyType),
                    $"{type.Name}.{property.Name} has type {property.PropertyType}, which is not an allowed public contract type.");
            }
        }
    }

    [Fact]
    public void PublicDtosLiveOnlyInThePublicContractNamespace()
    {
        foreach (var type in PublicTypes)
        {
            Assert.Equal("SmartProperty.Api.Contracts.Marketplace.Public", type.Namespace);
            Assert.True(type.IsSealed);
        }
    }

    [Fact]
    public void ListingIdIsTheOnlyIdentifier()
    {
        foreach (var type in PublicTypes)
        {
            var guidMembers = PropertiesOf(type)
                .Where(property => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(Guid))
                .Select(property => property.Name)
                .ToArray();

            if (type == typeof(PublicListingSummaryResponse) || type == typeof(PublicListingDetailResponse))
            {
                Assert.Equal(new[] { "ListingId" }, guidMembers);
            }
            else
            {
                Assert.Empty(guidMembers);
            }
        }
    }

    [Fact]
    public void PurposeAndPropertyTypeAreStringsNotDomainEnums()
    {
        foreach (var type in new[] { typeof(PublicListingSummaryResponse), typeof(PublicListingDetailResponse) })
        {
            Assert.Equal(typeof(string), type.GetProperty(nameof(PublicListingSummaryResponse.Purpose))!.PropertyType);
            Assert.Equal(typeof(string), type.GetProperty(nameof(PublicListingSummaryResponse.PropertyType))!.PropertyType);
        }
    }

    [Fact]
    public void TheDetailIsTheSummaryPlusTheDescription()
    {
        var summary = PropertiesOf(typeof(PublicListingSummaryResponse))
            .Select(property => (property.Name, property.PropertyType))
            .ToHashSet();
        var detail = PropertiesOf(typeof(PublicListingDetailResponse))
            .Select(property => (property.Name, property.PropertyType))
            .ToHashSet();

        Assert.True(summary.IsSubsetOf(detail));
        Assert.Equal(new[] { "Description" }, detail.Except(summary).Select(member => member.Name).ToArray());
    }

    [Fact]
    public void ThePriceHasExactlyAnAmountACurrencyAndAnOptionalRentPeriod()
    {
        var members = PropertiesOf(typeof(PublicMoneyResponse))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => (property.Name, property.PropertyType))
            .ToArray();

        Assert.Equal(
            new[]
            {
                (nameof(PublicMoneyResponse.Amount), typeof(decimal)),
                (nameof(PublicMoneyResponse.Currency), typeof(string)),
                (nameof(PublicMoneyResponse.RentPeriod), typeof(string)),
            },
            members);
    }

    [Fact]
    public void TheSummarySerializesToExactlyTheApprovedCamelCaseFields()
    {
        using var document = Serialize(SampleSummary());
        var root = document.RootElement;

        Assert.Equal(
            new[] { "listingId", "location", "price", "propertyType", "publishedAt", "purpose", "title" },
            SortedNames(root));
        Assert.Equal(new[] { "amount", "currency" }, SortedNames(root.GetProperty("price")));
        Assert.Equal(new[] { "city", "countryCode", "district", "region" }, SortedNames(root.GetProperty("location")));

        Assert.Equal(SampleListingId.ToString(), root.GetProperty("listingId").GetString());
        Assert.Equal("Sale", root.GetProperty("purpose").GetString());
        Assert.Equal("Unit", root.GetProperty("propertyType").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("publishedAt").ValueKind);
    }

    [Fact]
    public void TheDetailSerializesToExactlyTheApprovedCamelCaseFields()
    {
        using var document = Serialize(SampleDetail());
        var root = document.RootElement;

        Assert.Equal(
            new[] { "description", "listingId", "location", "price", "propertyType", "publishedAt", "purpose", "title" },
            SortedNames(root));
        Assert.Equal(new[] { "amount", "currency", "rentPeriod" }, SortedNames(root.GetProperty("price")));
        Assert.Equal(new[] { "city", "countryCode", "district", "region" }, SortedNames(root.GetProperty("location")));
        Assert.Equal("Rent", root.GetProperty("purpose").GetString());
        Assert.Equal("Monthly", root.GetProperty("price").GetProperty("rentPeriod").GetString());
    }

    [Fact]
    public void ASalePriceOmitsTheRentPeriodFromTheJsonEntirely()
    {
        var summaryJson = JsonSerializer.Serialize(SampleSummary(), WebOptions);
        var detailJson = JsonSerializer.Serialize(SampleDetail() with { Purpose = "Sale", Price = new PublicMoneyResponse(1250000m, "SAR") }, WebOptions);

        Assert.DoesNotContain("rentPeriod", summaryJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rentPeriod", detailJson, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(summaryJson);
        Assert.False(document.RootElement.GetProperty("price").TryGetProperty("rentPeriod", out _));
    }

    [Theory]
    [InlineData("Monthly")]
    [InlineData("Yearly")]
    public void ARentPriceCarriesItsRentPeriodInTheSummaryAndTheDetail(string rentPeriod)
    {
        var rentPrice = new PublicMoneyResponse(5200m, "SAR", rentPeriod);

        using var summary = Serialize(SampleSummary() with { Purpose = "Rent", Price = rentPrice });
        using var detail = Serialize(SampleDetail() with { Price = rentPrice });

        foreach (var document in new[] { summary, detail })
        {
            var price = document.RootElement.GetProperty("price");

            Assert.Equal(new[] { "amount", "currency", "rentPeriod" }, SortedNames(price));
            Assert.Equal(rentPeriod, price.GetProperty("rentPeriod").GetString());
            Assert.Equal(JsonValueKind.Number, price.GetProperty("amount").ValueKind);
            Assert.Equal("SAR", price.GetProperty("currency").GetString());
        }
    }

    [Fact]
    public void OptionalLocationPartsAreStillPresentWhenNull()
    {
        var summary = SampleSummary() with { Location = new PublicLocationResponse("SA", "Riyadh", null, null) };

        using var document = Serialize(summary);
        var location = document.RootElement.GetProperty("location");

        Assert.Equal(JsonValueKind.Null, location.GetProperty("region").ValueKind);
        Assert.Equal(JsonValueKind.Null, location.GetProperty("district").ValueKind);
    }

    [Fact]
    public void NoSerializedPropertyNameSuggestsPrivateDataAndAllAreCamelCase()
    {
        foreach (var document in new[] { Serialize(SampleSummary()), Serialize(SampleDetail()) })
        {
            using (document)
            {
                foreach (var name in AllPropertyNames(document.RootElement))
                {
                    Assert.True(char.IsLower(name[0]), $"'{name}' is not camelCase.");
                    AssertNameIsNotForbidden(name, "serialized JSON");
                }
            }
        }
    }

    [Fact]
    public void ASearchPageUsesTheExistingPagedResponseShape()
    {
        var page = new PagedResponse<PublicListingSummaryResponse>(
            [SampleSummary()],
            Page: 1,
            PageSize: 10,
            TotalCount: 1,
            TotalPages: 1,
            HasNext: false,
            HasPrevious: false);

        using var document = Serialize(page);
        var root = document.RootElement;

        Assert.Equal(
            new[] { "hasNext", "hasPrevious", "items", "page", "pageSize", "totalCount", "totalPages" },
            SortedNames(root));
        Assert.Equal(
            new[] { "listingId", "location", "price", "propertyType", "publishedAt", "purpose", "title" },
            SortedNames(root.GetProperty("items")[0]));
    }

    // A Sale listing: its price has no rent period.
    private static PublicListingSummaryResponse SampleSummary()
    {
        return new PublicListingSummaryResponse(
            SampleListingId,
            "Sale",
            "Corner villa",
            new PublicMoneyResponse(1250000m, "SAR"),
            "Unit",
            new PublicLocationResponse("SA", "Riyadh", "Riyadh Province", "Al Olaya"),
            new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
    }

    // A Rent listing: its price states the period it is charged for.
    private static PublicListingDetailResponse SampleDetail()
    {
        return new PublicListingDetailResponse(
            SampleListingId,
            "Rent",
            "Bright apartment",
            "Two bedrooms close to the centre.",
            new PublicMoneyResponse(5200m, "SAR", "Monthly"),
            "Unit",
            new PublicLocationResponse("SA", "Riyadh", "Riyadh Province", "Al Olaya"),
            new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
    }

    private static JsonDocument Serialize<T>(T value)
    {
        return JsonDocument.Parse(JsonSerializer.Serialize(value, WebOptions));
    }

    private static PropertyInfo[] PropertiesOf(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
    }

    private static bool IsAllowedMemberType(Type memberType)
    {
        var type = Nullable.GetUnderlyingType(memberType) ?? memberType;

        return type == typeof(string)
            || type == typeof(Guid)
            || type == typeof(decimal)
            || type == typeof(DateTimeOffset)
            || PublicTypes.Contains(type);
    }

    private static void AssertNameIsNotForbidden(string name, string owner)
    {
        foreach (var fragment in ForbiddenFragments)
        {
            Assert.False(
                name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                $"'{name}' ({owner}) contains the forbidden fragment '{fragment}'.");
        }
    }

    private static string[] SortedNames(JsonElement element)
    {
        return element.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> AllPropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;

                foreach (var nested in AllPropertyNames(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in AllPropertyNames(item))
                {
                    yield return nested;
                }
            }
        }
    }
}
