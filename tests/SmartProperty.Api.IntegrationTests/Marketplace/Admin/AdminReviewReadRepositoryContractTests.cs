using System.Reflection;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Detail;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Queue;
using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Marketplace.Admin;

/// <summary>
/// Pins the exact contract the Persistence implementation of the reviewer read repository must satisfy, and what the
/// handlers behind the review endpoints depend on, so neither can change unnoticed while that implementation is built.
/// </summary>
/// <remarks>
/// These tests are valid before and after the review reads are activated. They read no database and register nothing.
/// If one fails, the contract Persistence was handed has changed and the hand-off must be reissued.
/// </remarks>
public sealed class AdminReviewReadRepositoryContractTests
{
    [Fact]
    public void TheRepositoryHasExactlyTheTwoReadMembersAndNothingThatWrites()
    {
        var members = typeof(IReviewerListingSubmissionReadRepository)
            .GetMethods()
            .Select(method => $"{method.ReturnType.Name}<{string.Join(",", method.ReturnType.GetGenericArguments().Select(a => a.Name))}> "
                + $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "Task`1<ReviewDetailLookup> GetForReviewAsync(Guid submissionId, CancellationToken cancellationToken)",
                "Task`1<ReviewQueuePage> ListForReviewAsync(Nullable`1 purpose, ListingSubmissionStatus status, "
                    + "Int32 skip, Int32 take, CancellationToken cancellationToken)",
            },
            members);
        Assert.Empty(typeof(IReviewerListingSubmissionReadRepository).GetProperties());
    }

    [Fact]
    public void TheDetailOutcomesAreTheTwoApprovedAnswersWithStableValues()
    {
        Assert.Equal(
            new[] { "Found", "NotFound" },
            Enum.GetNames<ReviewDetailOutcome>());
        Assert.Equal(new[] { 1, 2 }, Enum.GetValues<ReviewDetailOutcome>().Select(value => (int)value).ToArray());
    }

    [Fact]
    public void TheReadModelsCarryExactlyTheApprovedMembers()
    {
        Assert.Equal(new[] { "Items", "TotalCount" }, Names(typeof(ReviewQueuePage)));
        Assert.Equal(
            new[]
            {
                "AllEvidenceClean", "EvidenceCount", "ListingId", "Location", "OwnerKind", "Price", "Purpose", "PropertyType",
                "Representation", "Status", "SubmissionId", "SubmittedAt", "Title", "Version",
            }.Order(StringComparer.Ordinal).ToArray(),
            Names(typeof(ReviewQueueItem)));
        Assert.Equal(
            new[]
            {
                "AllEvidenceClean", "AuthorityValidFrom", "AuthorityValidUntil", "CreatedAt", "Decision", "Description", "EvidenceCount",
                "IsCurrentlyPublished", "ListingId", "Location", "OwnerKind", "PredecessorSubmissionId", "Price", "PropertyId",
                "PropertyType", "Purpose", "Representation", "Status", "SubmissionId", "SubmittedAt", "Title", "UpdatedAt", "Version",
            }.Order(StringComparer.Ordinal).ToArray(),
            Names(typeof(ReviewSubmissionDetail)));
        Assert.Equal(new[] { "Approved", "DecidedAt", "Reason" }, Names(typeof(ReviewDecisionSummary)));
    }

    [Fact]
    public void NoReadModelCarriesALegalPartyReferenceADigestAnAssessmentOrAUserIdentity()
    {
        var forbidden = new[] { "LegalParty", "LinkedUser", "DeclaredOwner", "ActingUser", "SubmitterUser", "ReviewerUser", "Digest", "Sha", "Assessment", "Snapshot", "Workspace" };
        var names = new[] { typeof(ReviewQueuePage), typeof(ReviewQueueItem), typeof(ReviewSubmissionDetail), typeof(ReviewDecisionSummary) }
            .SelectMany(Names)
            .ToArray();

        foreach (var term in forbidden)
        {
            Assert.DoesNotContain(names, name => name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void BothReadHandlersDependOnExactlyTheUserTheAuthorizerAndTheRepository()
    {
        var expected = new[] { typeof(ICurrentUser), typeof(IReviewReadAuthorizer), typeof(IReviewerListingSubmissionReadRepository) };

        foreach (var handler in new[] { typeof(GetReviewQueueQueryHandler), typeof(GetReviewSubmissionQueryHandler) })
        {
            var constructor = Assert.Single(handler.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

            Assert.Equal(
                expected.Select(type => type.FullName).Order(StringComparer.Ordinal).ToArray(),
                constructor.GetParameters().Select(parameter => parameter.ParameterType.FullName).Order(StringComparer.Ordinal).ToArray());
        }
    }

    private static string[] Names(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
