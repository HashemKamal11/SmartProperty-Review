using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

/// <summary>
/// The write side of listing submissions. Implemented in Persistence; the locking, the trust boundary and the
/// atomicity below are the implementation's obligations, not the caller's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lock order.</b> Every writer takes locks in one order, so no two can wait on each other:
/// the registry property row first (<see cref="LockPropertyAsync"/>), then the grant row shared
/// (<see cref="GetGrantForSharedLockAsync"/>; revoking a grant takes it exclusively), then the listing row
/// (<see cref="GetListingAsync"/>), then submissions. A method that needs a lock the caller has not yet taken
/// must fail rather than take it out of order.
/// </para>
/// <para>
/// <b>Trust boundary.</b> <see cref="GetGrantForSharedLockAsync"/> returns a grant read from persistence inside the
/// current transaction, after the property lock. It is the only source of an <see cref="AuthorityGrantView"/>. The
/// Application evaluates it and never trusts a grant it did not just read this way.
/// </para>
/// <para>
/// <b>Atomicity.</b> Writes are tracked, not flushed. <see cref="SaveChangesAsync"/> commits everything tracked,
/// including audit events recorded through <see cref="IListingSubmissionAuditRecorder"/>, in one database
/// transaction, and translates a lost race on a unique constraint into <see cref="ListingWriteConflictException"/>.
/// Reads of drafts return only the acting user's own; another user's draft and a missing one are the same
/// <see langword="null"/>.
/// </para>
/// </remarks>
public interface IListingSubmissionWriteRepository
{
    /// <summary>
    /// Locks the registry property row exclusively and returns its canonical facts, or null when it does not exist.
    /// </summary>
    Task<LockedPropertyView?> LockPropertyAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The grant for this grantee, property and purpose, read under a shared lock, or null when there is none. The
    /// property lock must already be held.
    /// </summary>
    Task<AuthorityGrantView?> GetGrantForSharedLockAsync(
        Guid granteeUserId,
        Guid propertyId,
        ListingPurpose purpose,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The listing for this property and purpose, locked, or null when none exists. The property lock must already
    /// be held.
    /// </summary>
    Task<PropertyListing?> GetListingAsync(
        Guid propertyId,
        ListingPurpose purpose,
        CancellationToken cancellationToken = default);

    Task AddListingAsync(PropertyListing listing, CancellationToken cancellationToken = default);

    /// <summary>
    /// The listing's most recent submission by version, whatever its status, or null when it has none.
    /// </summary>
    Task<ListingSubmission?> GetLatestSubmissionAsync(Guid listingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The acting user's own submission with this id, or null when it does not exist or belongs to someone else.
    /// </summary>
    Task<ListingSubmission?> GetOwnSubmissionAsync(
        Guid submissionId,
        Guid actingUserId,
        CancellationToken cancellationToken = default);

    Task AddSubmissionAsync(ListingSubmission submission, CancellationToken cancellationToken = default);

    /// <summary>Removes a draft that was never submitted. History and every submitted version are untouched.</summary>
    Task RemoveDraftAsync(ListingSubmission draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the listing may be removed once the given draft is gone: it would have no submissions, was never
    /// published or withdrawn, is not closed or reserved, and nothing references it.
    /// </summary>
    Task<bool> CanRemoveListingAfterDraftAsync(
        Guid listingId,
        Guid draftSubmissionId,
        CancellationToken cancellationToken = default);

    Task RemoveListingAsync(PropertyListing listing, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits all tracked changes in one transaction, or none of them.
    /// </summary>
    /// <exception cref="ListingWriteConflictException">A concurrent writer won a unique-constraint race.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
