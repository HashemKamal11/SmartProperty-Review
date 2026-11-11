using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Abstractions.Marketplace;

/// <summary>
/// The trusted facts about a reviewer that decide whether they may rule on one submission.
/// </summary>
/// <remarks>
/// A null is "unknown", and the Domain treats unknown as not independent. The values are never read from a request.
/// </remarks>
public sealed record ReviewerIndependenceFacts(
    bool? IdentitiesVerified,
    bool? ReviewerHasOwnerConflict,
    bool? ReviewerAffiliatedWithWorkspace);

/// <summary>
/// The one source of reviewer-independence facts.
/// </summary>
/// <remarks>
/// <para>
/// Establishing that a reviewer is not the owner, not a representative or controlling person of an owning organisation
/// and not affiliated with the submitting workspace needs a verified registry of legal parties and of identities.
/// That registry does not exist in this system yet, so the only implementation registered answers "unknown" for every
/// fact and every decision is refused until a real provider replaces it. This is a documented production blocker; it
/// is not worked around by guessing.
/// </para>
/// <para>
/// A failure to determine the facts must be an exception, never an optimistic answer. The submitter is passed so a
/// provider can check the reviewer against the person who wrote the submission as well as the declared owner.
/// </para>
/// </remarks>
public interface IReviewerIndependenceProvider
{
    Task<ReviewerIndependenceFacts> GetAsync(
        Guid reviewerUserId,
        Guid submitterUserId,
        ListingOwnerIdentity declaredOwner,
        Guid? submittingWorkspaceId,
        CancellationToken cancellationToken = default);
}
