using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Api.Infrastructure.Marketplace;

/// <summary>
/// The production reviewer-independence source until a verified registry of identities and legal parties exists: it
/// can establish nothing.
/// </summary>
/// <remarks>
/// Every fact is unknown, and the Domain treats unknown as not independent, so every decision is refused with
/// <c>listing_submissions.independence_unverifiable</c>. That is intended and is a documented production blocker; it
/// is not worked around by assuming independence. A provider that answers is for tests only.
/// </remarks>
internal sealed class UnavailableReviewerIndependenceProvider : IReviewerIndependenceProvider
{
    public Task<ReviewerIndependenceFacts> GetAsync(
        Guid reviewerUserId,
        Guid submitterUserId,
        ListingOwnerIdentity declaredOwner,
        Guid? submittingWorkspaceId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ReviewerIndependenceFacts(
            IdentitiesVerified: null,
            ReviewerHasOwnerConflict: null,
            ReviewerAffiliatedWithWorkspace: null));
    }
}
