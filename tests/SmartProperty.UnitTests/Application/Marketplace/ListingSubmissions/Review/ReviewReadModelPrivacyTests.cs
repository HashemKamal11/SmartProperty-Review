using System.Reflection;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Detail;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Queue;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Review;

/// <summary>
/// What the initial Platform Admin review read models can and cannot carry, and the security gate that keeps the
/// handlers inert until an approved Platform permission exists. The read models are a closed vocabulary: every
/// member is a plain value, a public enum or another read model, so a confidential field cannot be added without
/// failing here and being reviewed.
/// </summary>
public sealed class ReviewReadModelPrivacyTests
{
    private static readonly Type[] ReadModels =
    [
        typeof(ReviewQueueItem),
        typeof(ReviewQueuePage),
        typeof(ReviewSubmissionDetail),
        typeof(ReviewDecisionSummary),
        typeof(ReviewDetailLookup),
        typeof(PublicListingPrice),
        typeof(PublicListingLocation),
    ];

    // Name fragments that would indicate confidential or privileged data. Evidence is handled separately below,
    // because the count and the aggregate flag are allowed and nothing else about evidence is.
    private static readonly string[] ForbiddenFragments =
    [
        "legalparty",
        "linkeduser",
        "declaredowner",
        "actinguser",
        "submitteruser",
        "userid",
        "workspace",
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
        "snapshot",
        "concurrency",
        "storage",
        "email",
        "phone",
        "token",
        "secret",
        "password",
        "uri",
        "url",
    ];

    [Fact]
    public void NoMemberNameSuggestsConfidentialData()
    {
        foreach (var type in ReadModels)
        {
            var names = PropertiesOf(type).Select(property => property.Name)
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .SelectMany(constructor => constructor.GetParameters())
                    .Select(parameter => parameter.Name ?? string.Empty));

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
    public void EvidenceIsOnlyEverACountAndOneAggregateFlag()
    {
        var evidenceMembers = ReadModels
            .SelectMany(type => PropertiesOf(type).Select(property => (TypeName: type.Name, MemberName: property.Name, MemberType: property.PropertyType)))
            .Where(member => member.MemberName.Contains("evidence", StringComparison.OrdinalIgnoreCase))
            .Select(member => $"{member.TypeName}.{member.MemberName}:{member.MemberType.Name}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "ReviewQueueItem.AllEvidenceClean:Boolean",
                "ReviewQueueItem.EvidenceCount:Int32",
                "ReviewSubmissionDetail.AllEvidenceClean:Boolean",
                "ReviewSubmissionDetail.EvidenceCount:Int32",
            },
            evidenceMembers);
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
    public void TheOnlyIdentifiersAreTheSubmissionTheListingThePropertyAndThePredecessor()
    {
        var item = GuidMembers(typeof(ReviewQueueItem));
        var detail = GuidMembers(typeof(ReviewSubmissionDetail));

        Assert.Equal(new[] { "ListingId", "SubmissionId" }, item);
        Assert.Equal(new[] { "ListingId", "PredecessorSubmissionId", "PropertyId", "SubmissionId" }, detail);

        foreach (var type in new[] { typeof(ReviewDecisionSummary), typeof(ReviewQueuePage), typeof(PublicListingPrice), typeof(PublicListingLocation) })
        {
            Assert.Empty(GuidMembers(type));
        }
    }

    [Fact]
    public void TheDecisionCarriesOnlyTheOutcomeTheReasonAndTheTime()
    {
        var members = PropertiesOf(typeof(ReviewDecisionSummary))
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Approved", "DecidedAt", "Reason" }, members);
    }

    [Fact]
    public void TheDetailIsTheQueueFieldsPlusTheReviewContext()
    {
        var item = PropertiesOf(typeof(ReviewQueueItem)).Select(property => property.Name).ToHashSet();
        var detail = PropertiesOf(typeof(ReviewSubmissionDetail)).Select(property => property.Name).ToHashSet();

        Assert.True(item.IsSubsetOf(detail));
        Assert.Equal(
            new[]
            {
                "AuthorityValidFrom", "AuthorityValidUntil", "CreatedAt", "Decision", "Description", "IsCurrentlyPublished",
                "PredecessorSubmissionId", "PropertyId", "UpdatedAt",
            },
            detail.Except(item).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void WhoIsAskingIsNeverARequestInput()
    {
        foreach (var query in new[] { typeof(GetReviewQueueQuery), typeof(GetReviewSubmissionQuery) })
        {
            var names = query.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.Name!).ToArray();

            foreach (var name in names)
            {
                foreach (var fragment in new[] { "user", "reviewer", "owner", "workspace", "legal", "property", "submitter", "acting", "search", "text" })
                {
                    Assert.False(
                        name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                        $"{query.Name} must not take '{name}': identity comes from the authenticated principal.");
                }
            }
        }

        Assert.Equal(
            new[] { "Page", "PageSize", "Purpose", "Status" },
            typeof(GetReviewQueueQuery).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name!).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { "SubmissionId" },
            typeof(GetReviewSubmissionQuery).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name!).ToArray());
    }

    [Fact]
    public void TheRepositoryContractHasExactlyTheTwoReadsAndTakesTheReviewerFirst()
    {
        var methods = typeof(IReviewerListingSubmissionReadRepository).GetMethods();

        Assert.Equal(
            new[] { "GetForReviewAsync", "ListForReviewAsync" },
            methods.Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        foreach (var method in methods)
        {
            var first = method.GetParameters().First();

            Assert.Equal("reviewerUserId", first.Name);
            Assert.Equal(typeof(Guid), first.ParameterType);
        }
    }

    [Fact]
    public void TheRepositoryReturnsOnlyReadModels()
    {
        foreach (var method in typeof(IReviewerListingSubmissionReadRepository).GetMethods())
        {
            var result = method.ReturnType.GetGenericArguments().Single();

            Assert.Contains(result, ReadModels);
        }
    }

    [Fact]
    public void TheDetailOutcomeVocabularyIsExactlyFoundNotFoundNotIndependentAndUnverifiable()
    {
        Assert.Equal(
            new[] { "Found", "NotFound", "NotIndependent", "IndependenceUnverifiable" },
            Enum.GetNames<ReviewDetailOutcome>());
    }

    [Fact]
    public void BothHandlersCannotBeBuiltWithoutTheAuthorizationSeam()
    {
        foreach (var handler in new[] { typeof(GetReviewQueueQueryHandler), typeof(GetReviewSubmissionQueryHandler) })
        {
            var parameters = handler.GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType).ToArray();

            Assert.Contains(typeof(IReviewReadAuthorizer), parameters);
        }
    }

    [Fact]
    public void TheAuthorizationSeamHasExactlyOneMethodTakingTheReviewerAndNoImplementationExistsInTheApplication()
    {
        var method = Assert.Single(typeof(IReviewReadAuthorizer).GetMethods());

        Assert.Equal("IsAuthorizedAsync", method.Name);
        Assert.Equal(typeof(Task<bool>), method.ReturnType);
        Assert.Equal("reviewerUserId", method.GetParameters().First().Name);

        // The gate: nothing in the Application assembly implements the seam, so no handler can be resolved from
        // production DI until an approved Platform permission exists and its implementation is deliberately added.
        var implementations = typeof(IReviewReadAuthorizer).Assembly.GetTypes()
            .Where(type => !type.IsInterface && typeof(IReviewReadAuthorizer).IsAssignableFrom(type))
            .Select(type => type.FullName)
            .ToArray();

        Assert.DoesNotContain(implementations, name => name is not null);
    }

    private static string[] GuidMembers(Type type)
    {
        return PropertiesOf(type)
            .Where(property => (Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) == typeof(Guid))
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
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
            return type.GetGenericArguments()[0] == typeof(ReviewQueueItem);
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
            || type == typeof(ListingOwnerKind)
            || type == typeof(PropertyType)
            || type == typeof(RentPeriod)
            || type == typeof(ReviewDetailOutcome)
            || ReadModels.Contains(type);
    }
}
