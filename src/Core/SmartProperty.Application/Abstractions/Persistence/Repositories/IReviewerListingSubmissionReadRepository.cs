using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

/// <summary>
/// Read side of the Platform Admin review of listing submissions. Implemented in Persistence; every rule below is
/// enforced in the query itself, never by the caller.
/// </summary>
/// <remarks>
/// <c>reviewerUserId</c> always comes from the authenticated principal and is used only to establish independence;
/// it is never a filter the caller chooses. Authorization to read at all is decided before the repository is
/// called, by <see cref="Authorization.IReviewReadAuthorizer"/>.
///
/// The queue never contains a Draft, and never contains a submission the reviewer acted on or is the linked owner
/// of. A detail lookup reports, as an explicit outcome, when the reviewer is not independent of the submission or
/// when independence cannot be established from the available facts; it never reports "found" in either case.
///
/// Results are projected into the read models only. Submissions, snapshots and decisions are immutable and are
/// read, never changed. No legal-party reference, evidence digest, internal assessment, reviewer snapshot or
/// user identity is selected. Filtering, counting, ordering and paging all happen in the database. Ordering is
/// deterministic: a pending queue is oldest submitted first, any other status is most recently updated first, and
/// every order ends in the submission id.
/// </remarks>
public interface IReviewerListingSubmissionReadRepository
{
    /// <summary>
    /// One page of submissions awaiting or having had review, narrowed by status (never Draft) and optionally by
    /// purpose, plus the total number of matches.
    /// </summary>
    Task<ReviewQueuePage> ListForReviewAsync(
        Guid reviewerUserId,
        ListingPurpose? purpose,
        ListingSubmissionStatus status,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The submission for review, or the reason it cannot be shown to this reviewer.
    /// </summary>
    Task<ReviewDetailLookup> GetForReviewAsync(
        Guid reviewerUserId,
        Guid submissionId,
        CancellationToken cancellationToken = default);
}
