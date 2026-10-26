using System.Reflection;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.PublicListings;

/// <summary>
/// What the public read models can and cannot carry. They are a closed vocabulary: every member is a plain value, a
/// public enum or another read model, so a private identifier, status or address field cannot be added without
/// failing here and being reviewed.
/// </summary>
public sealed class PublicListingReadModelPrivacyTests
{
    private static readonly Type[] ReadModels =
    [
        typeof(PublicListingPrice),
        typeof(PublicListingLocation),
        typeof(PublicListingSummary),
        typeof(PublicListingDetail),
        typeof(PublicListingPage),
    ];

    // Name fragments that would indicate private, internal or privileged data.
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
        "storage",
        "concurrency",
        "reservation",
    ];

    [Fact]
    public void NoMemberNameSuggestsPrivateData()
    {
        foreach (var type in ReadModels)
        {
            var names = PropertiesOf(type).Select(property => property.Name)
                .Concat(type.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.Name ?? string.Empty));

            foreach (var name in names)
            {
                foreach (var fragment in ForbiddenFragments)
                {
                    Assert.False(
                        name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                        $"'{name}' ({type.Name}) contains the forbidden fragment '{fragment}'.");
                }
            }
        }
    }

    [Fact]
    public void EveryMemberIsAPlainValueAPublicEnumOrAnotherReadModel()
    {
        foreach (var type in ReadModels)
        {
            foreach (var property in PropertiesOf(type))
            {
                Assert.True(
                    IsAllowedMemberType(property.PropertyType),
                    $"{type.Name}.{property.Name} has type {property.PropertyType}, which is not an allowed read-model type.");
            }
        }
    }

    [Fact]
    public void ListingIdIsTheOnlyIdentifier()
    {
        foreach (var type in ReadModels)
        {
            var guidMembers = PropertiesOf(type)
                .Where(property => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(Guid))
                .Select(property => property.Name)
                .ToArray();

            if (type == typeof(PublicListingSummary) || type == typeof(PublicListingDetail))
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
    public void TheDetailIsTheSummaryPlusTheDescription()
    {
        var summary = PropertiesOf(typeof(PublicListingSummary)).Select(property => (property.Name, property.PropertyType)).ToHashSet();
        var detail = PropertiesOf(typeof(PublicListingDetail)).Select(property => (property.Name, property.PropertyType)).ToHashSet();

        Assert.True(summary.IsSubsetOf(detail));
        Assert.Equal(new[] { "Description" }, detail.Except(summary).Select(member => member.Name).ToArray());
    }

    [Fact]
    public void TheLocationAndTextMembersAreNeverNull()
    {
        var context = new NullabilityInfoContext();

        foreach (var type in new[] { typeof(PublicListingLocation), typeof(PublicListingSummary), typeof(PublicListingDetail), typeof(PublicListingPrice) })
        {
            foreach (var property in PropertiesOf(type).Where(property => property.PropertyType == typeof(string)))
            {
                Assert.Equal(
                    NullabilityState.NotNull,
                    context.Create(property).ReadState);
            }
        }
    }

    [Fact]
    public void OnlyThePriceMayCarryAnOptionalRentPeriod()
    {
        foreach (var type in ReadModels)
        {
            var optionalRentPeriods = PropertiesOf(type)
                .Where(property => Nullable.GetUnderlyingType(property.PropertyType) == typeof(RentPeriod))
                .Select(property => $"{type.Name}.{property.Name}")
                .ToArray();

            if (type == typeof(PublicListingPrice))
            {
                Assert.Equal(new[] { "PublicListingPrice.RentPeriod" }, optionalRentPeriods);
            }
            else
            {
                Assert.Empty(optionalRentPeriods);
            }
        }
    }

    [Fact]
    public void TheRepositoryContractHasExactlyTheTwoPublicReads()
    {
        var methods = typeof(IPublicListingReadRepository)
            .GetMethods()
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "GetByIdAsync", "SearchAsync" }, methods);
    }

    [Fact]
    public void TheRepositoryReturnsOnlyReadModels()
    {
        foreach (var method in typeof(IPublicListingReadRepository).GetMethods())
        {
            var result = method.ReturnType.GetGenericArguments().Single();

            Assert.Contains(result, ReadModels);
        }
    }

    [Fact]
    public void TheRentPeriodVocabularyIsExactlyMonthlyAndYearly()
    {
        Assert.Equal(new[] { "Monthly", "Yearly" }, Enum.GetNames<RentPeriod>());
    }

    [Fact]
    public void TheListingPurposeVocabularyIsExactlySaleAndRent()
    {
        Assert.Equal(new[] { "Sale", "Rent" }, Enum.GetNames<ListingPurpose>());
    }

    [Fact]
    public void TheSortOrderVocabularyIsExactlyNewestAndThePriceOrders()
    {
        Assert.Equal(new[] { "Newest", "PriceAsc", "PriceDesc" }, Enum.GetNames<PublicListingSortOrder>());
    }

    private static PropertyInfo[] PropertiesOf(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
    }

    private static bool IsAllowedMemberType(Type memberType)
    {
        var type = Nullable.GetUnderlyingType(memberType) ?? memberType;

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            return type.GetGenericArguments()[0] == typeof(PublicListingSummary);
        }

        return type == typeof(string)
            || type == typeof(Guid)
            || type == typeof(decimal)
            || type == typeof(long)
            || type == typeof(DateTimeOffset)
            || type == typeof(ListingPurpose)
            || type == typeof(PropertyType)
            || type == typeof(RentPeriod)
            || ReadModels.Contains(type);
    }
}
