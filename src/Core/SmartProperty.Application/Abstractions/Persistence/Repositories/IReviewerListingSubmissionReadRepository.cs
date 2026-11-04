using SmartProperty.Application.Marketplace.ListingSubmissions.Review;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

/// <summary>
/// Read side of the Platform Admin review of listing submissions. Implemented in Persistence; every rule below is
/// enforced in the query itself, never by the caller.
/// </summary>
/// <remarks>
/// The repository receives no user identity. Authorization to read at all is decided before it is called, by
/// <see cref="Authorization.IReviewReadAuthorizer"/>, and reading does not depend on reviewer independence: an
/// authorized reader sees the queue and the redacted detail whoever submitted or owns the submission. Independence
/// governs decisions only: any future Approve/Reject operation must enforce it from locked reads. No decision
/// operation exists in this build.
///
/// The queue never contains a Draft. A detail lookup is either found or not found.
///
/// Results are projected into the read models only. Submissions, snapshots and decisions are immutable and are
/// read, never changed. No legal-party reference, evidence digest, internal assessment, reviewer snapshot, user identity
/// or rejection reason is selected. Filtering, counting, ordering and paging all happen in the database. Ordering is
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
        ListingPurpose? purpose,
        ListingSubmissionStatus status,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The submission for review, or that it was not found.
    /// </summary>
    Task<ReviewDetailLookup> GetForReviewAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default);
}
