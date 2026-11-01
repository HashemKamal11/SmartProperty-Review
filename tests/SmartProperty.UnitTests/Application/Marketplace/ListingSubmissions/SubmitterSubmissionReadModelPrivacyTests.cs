using System.Reflection;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.GetById;
using SmartProperty.Application.Marketplace.ListingSubmissions.Mine;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions;

/// <summary>
/// What the submitter read models can and cannot carry. They are a closed vocabulary: every member is a plain
/// value, a public enum or another read model, so a property id, legal-party reference, Workspace id, evidence,
/// digest, verification fact or reviewer identity cannot be added without failing here and being reviewed.
/// </summary>
public sealed class SubmitterSubmissionReadModelPrivacyTests
{
    private static readonly Type[] ReadModels =
    [
        typeof(SubmitterSubmissionSummary),
        typeof(SubmitterSubmissionDetail),
        typeof(SubmitterSubmissionDecision),
        typeof(SubmitterSubmissionPage),
        typeof(PublicListingPrice),
        typeof(PublicListingLocation),
    ];

    // Name fragments that would indicate private, internal or privileged data.
    private static readonly string[] ForbiddenFragments =
    [
        "propertyid",
        "legalparty",
        "linkeduser",
        "declaredowner",
        "ownerid",
        "owneridentity",
        "workspace",
        "evidence",
        "sha",
        "digest",
        "hash",
        "assessment",
        "scan",
        "verif",
        "reviewer",
        "reviewcontext",
        "independence",
        "affiliat",
        "conflict",
        "actinguser",
        "submitteruser",
        "userid",
        "predecessor",
        "concurrency",
        "snapshot",
        "storage",
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
    public void SubmissionIdAndListingIdAreTheOnlyIdentifiers()
    {
        foreach (var type in new[] { typeof(SubmitterSubmissionSummary), typeof(SubmitterSubmissionDetail) })
        {
            var guids = PropertiesOf(type)
                .Where(property => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(Guid))
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(new[] { "ListingId", "SubmissionId" }, guids);
        }

        foreach (var type in new[] { typeof(SubmitterSubmissionDecision), typeof(SubmitterSubmissionPage), typeof(PublicListingPrice), typeof(PublicListingLocation) })
        {
            Assert.DoesNotContain(PropertiesOf(type), property => property.PropertyType == typeof(Guid));
        }
    }

    [Fact]
    public void TheDecisionCarriesOnlyTheOutcomeTheReasonAndTheTime()
    {
        var members = PropertiesOf(typeof(SubmitterSubmissionDecision))
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Approved", "DecidedAt", "Reason" }, members);
    }

    [Fact]
    public void TheDetailIsTheSummaryPlusTheDescriptionAndTheDecision()
    {
        var summary = PropertiesOf(typeof(SubmitterSubmissionSummary)).Select(property => (property.Name, property.PropertyType)).ToHashSet();
        var detail = PropertiesOf(typeof(SubmitterSubmissionDetail)).Select(property => (property.Name, property.PropertyType)).ToHashSet();

        Assert.True(summary.IsSubsetOf(detail));
        Assert.Equal(
            new[] { "Decision", "Description" },
            detail.Except(summary).Select(member => member.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void WhoseSubmissionsToReadIsNeverARequestInput()
    {
        foreach (var query in new[] { typeof(GetMyListingSubmissionsQuery), typeof(GetMyListingSubmissionByIdQuery) })
        {
            var names = query.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.Name!).ToArray();

            foreach (var name in names)
            {
                foreach (var fragment in new[] { "user", "owner", "workspace", "legal", "property", "submitter", "acting" })
                {
                    Assert.False(
                        name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                        $"{query.Name} must not take '{name}': identity comes from the authenticated principal.");
                }
            }
        }

        Assert.Equal(
            new[] { "Page", "PageSize", "Purpose", "Status" },
            typeof(GetMyListingSubmissionsQuery).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name!).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { "SubmissionId" },
            typeof(GetMyListingSubmissionByIdQuery).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name!).ToArray());
    }

    [Fact]
    public void TheRepositoryContractHasExactlyTheTwoScopedReadsAndTakesTheActingUserFirst()
    {
        var methods = typeof(ISubmitterListingSubmissionReadRepository).GetMethods();

        Assert.Equal(
            new[] { "GetBySubmitterAsync", "ListBySubmitterAsync" },
            methods.Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        foreach (var method in methods)
        {
            var first = method.GetParameters().First();

            Assert.Equal("actingUserId", first.Name);
            Assert.Equal(typeof(Guid), first.ParameterType);
        }
    }

    [Fact]
    public void TheRepositoryReturnsOnlyReadModels()
    {
        foreach (var method in typeof(ISubmitterListingSubmissionReadRepository).GetMethods())
        {
            var result = method.ReturnType.GetGenericArguments().Single();

            Assert.Contains(result, ReadModels);
        }
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
            return type.GetGenericArguments()[0] == typeof(SubmitterSubmissionSummary);
        }

        return type == typeof(string)
            || type == typeof(Guid)
            || type == typeof(decimal)
            || type == typeof(int)
            || type == typeof(long)
            || type == typeof(bool)
            || type == typeof(DateTimeOffset)
            || type == typeof(ListingPurpose)
            || type == typeof(ListingSubmissionStatus)
            || type == typeof(ListingRepresentationKind)
            || type == typeof(PropertyType)
            || type == typeof(RentPeriod)
            || ReadModels.Contains(type);
    }
}
