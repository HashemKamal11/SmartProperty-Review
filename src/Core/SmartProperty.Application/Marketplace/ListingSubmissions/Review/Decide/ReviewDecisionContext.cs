using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Review.Decide;

/// <summary>Everything a decision reads under the locks, and nothing it read before them.</summary>
internal sealed record LockedDecision(
    ListingSubmission Submission,
    PropertyListing Listing,
    ListingSubmission? CurrentPublication,
    LockedPropertyView Property,
    ListingEligibility SubmitterEligibility);

/// <summary>
/// Takes the locks a review decision needs in the one fixed order: property, the submitter's grant for the purpose
/// (shared), the listing, then the submissions.
/// </summary>
/// <remarks>
/// The submission is first read only to learn which property, listing and submitter to lock; nothing is decided from
/// that read. The grant is evaluated here but not required by the lock itself: a rejection does not depend on it, an
/// approval does.
/// </remarks>
internal static class ReviewDecisionContext
{
    public static async Task<Result<LockedDecision>> LockAsync(
        IListingSubmissionWriteRepository repository,
        Guid submissionId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var located = await repository.GetSubmissionAsync(submissionId, cancellationToken);

        if (located is null)
        {
            return Result<LockedDecision>.Failure(ListingSubmissionErrors.NotFound);
        }

        var property = await repository.LockPropertyAsync(located.PropertyId, cancellationToken);

        if (property is null || property.PropertyId != located.PropertyId)
        {
            return Result<LockedDecision>.Failure(ListingSubmissionErrors.NotFound);
        }

        var submitterId = located.Authority.ActingUserId;
        var grant = await repository.GetGrantForSharedLockAsync(submitterId, located.PropertyId, located.Purpose, cancellationToken);
        var eligibility = GrantEligibilityEvaluator.Evaluate(grant, submitterId, located.PropertyId, located.Purpose, at);

        var listing = await repository.GetListingByIdAsync(located.ListingId, cancellationToken);
        var submission = await repository.GetSubmissionAsync(submissionId, cancellationToken);

        if (listing is null
            || submission is null
            || submission.ListingId != listing.Id
            || submission.PropertyId != located.PropertyId
            || submission.Purpose != located.Purpose
            || listing.PropertyId != located.PropertyId
            || listing.Purpose != located.Purpose)
        {
            return Result<LockedDecision>.Failure(ListingSubmissionErrors.NotFound);
        }

        ListingSubmission? current = null;

        if (listing.PublishedSubmissionId is { } publishedId)
        {
            current = await repository.GetSubmissionAsync(publishedId, cancellationToken);
        }

        return Result<LockedDecision>.Success(new LockedDecision(submission, listing, current, property, eligibility));
    }

    /// <summary>
    /// Builds the reviewer facts from the trusted provider and the submission, and refuses a reviewer who is not
    /// independent. Unknown is refused as unverifiable; a known conflict is refused as not independent.
    /// </summary>
    public static async Task<Result<ListingReviewContext>> ReviewerAsync(
        IReviewerIndependenceProvider provider,
        ListingSubmission submission,
        Guid reviewerUserId,
        CancellationToken cancellationToken)
    {
        var authority = submission.Authority;
        var facts = await provider.GetAsync(reviewerUserId, authority.ActingUserId, authority.DeclaredOwner, authority.WorkspaceId, cancellationToken);

        ArgumentNullException.ThrowIfNull(facts);

        if (reviewerUserId == authority.ActingUserId || reviewerUserId == authority.DeclaredOwner.LinkedUserId)
        {
            return Result<ListingReviewContext>.Failure(ReviewErrors.NotIndependent);
        }

        var context = new ListingReviewContext(
            reviewerUserId,
            authority.ActingUserId,
            authority.DeclaredOwner,
            authority.WorkspaceId,
            facts.IdentitiesVerified,
            platformReviewAuthorized: true,
            authority.WorkspaceId is null ? null : facts.ReviewerAffiliatedWithWorkspace,
            facts.ReviewerHasOwnerConflict);

        if (facts.ReviewerHasOwnerConflict == true || facts.ReviewerAffiliatedWithWorkspace == true)
        {
            return Result<ListingReviewContext>.Failure(ReviewErrors.NotIndependent);
        }

        try
        {
            context.EnsureIndependentReviewer();
        }
        catch (InvalidOperationException)
        {
            return Result<ListingReviewContext>.Failure(ReviewErrors.IndependenceUnverifiable);
        }

        return Result<ListingReviewContext>.Success(context);
    }
}
