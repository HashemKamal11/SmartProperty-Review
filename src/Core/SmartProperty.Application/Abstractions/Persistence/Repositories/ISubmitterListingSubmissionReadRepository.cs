using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

/// <summary>
/// Read side of a submitter's own listing submissions. Implemented in Persistence; the ownership rule is enforced
/// in the query itself, never by the caller.
/// </summary>
/// <remarks>
/// A submission belongs to the user who acted on it: the acting user recorded in its legal-authority declaration.
/// That is the submitter's identity, not proof of legal ownership, and it is never derived from the declared owner,
/// the owner's linked user, Workspace membership or any caller-supplied value. <c>actingUserId</c> always comes
/// from the authenticated principal.
///
/// Every query is scoped to <c>actingUserId</c> in the database. A submission that belongs to another user is
/// indistinguishable from one that does not exist: the detail read returns <see langword="null"/> for both, and the
/// list never contains it. Results are projected into the read models only, so no property id, legal-party
/// reference, Workspace id, evidence, digest, verification fact or reviewer identity is ever selected. Filtering,
/// counting, ordering (newest first, ending in the submission id) and paging all happen in the database.
/// </remarks>
public interface ISubmitterListingSubmissionReadRepository
{
    /// <summary>
    /// One page of the acting user's own submissions, newest first, optionally narrowed by purpose and status.
    /// </summary>
    Task<SubmitterSubmissionPage> ListBySubmitterAsync(
        Guid actingUserId,
        ListingPurpose? purpose,
        ListingSubmissionStatus? status,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The acting user's own submission with the given id, or <see langword="null"/> when it does not exist or
    /// belongs to someone else.
    /// </summary>
    Task<SubmitterSubmissionDetail?> GetBySubmitterAsync(
        Guid actingUserId,
        Guid submissionId,
        CancellationToken cancellationToken = default);
}
