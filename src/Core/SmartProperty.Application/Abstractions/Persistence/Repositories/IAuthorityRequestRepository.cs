using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

/// <summary>A document version with the facts of its request that evidence resolution needs.</summary>
public sealed record AuthorityDocumentEvidenceRow(
    AuthorityRequestDocument Document,
    Guid RequesterUserId,
    Guid PropertyId,
    AuthorityRequestStatus RequestStatus,
    bool RequestsSale,
    bool RequestsRent);

/// <summary>Authority requests with their document versions and decisions, and the grants they lead to.</summary>
public interface IAuthorityRequestRepository
{
    /// <summary>The request with its documents and decisions, tracked, or null.</summary>
    Task<AuthorityRequest?> GetAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>The owner's requests, newest first, optionally for one property. Not tracked.</summary>
    Task<IReadOnlyList<AuthorityRequest>> ListForOwnerAsync(
        Guid ownerUserId, Guid? propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of requests that have left Draft, for staff. Waiting ones (legal review, issuance) are oldest first; the
    /// rest newest first. Not tracked.
    /// </summary>
    Task<(IReadOnlyList<AuthorityRequest> Items, int TotalCount)> ListForStaffAsync(
        AuthorityRequestStatus? status, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Whether the property has an open listing request (ownership-transfer requests are not counted).</summary>
    Task<bool> HasOpenRequestAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>Whether this claimant already has an open ownership-transfer request for this property.</summary>
    Task<bool> HasOpenTransferRequestAsync(Guid propertyId, Guid requesterUserId, CancellationToken cancellationToken = default);

    /// <summary>Every open request of any kind on the property, with documents and decisions, tracked.</summary>
    Task<IReadOnlyList<AuthorityRequest>> GetOpenRequestsForPropertyAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>Every never-revoked grant on the property, of any grantee, tracked.</summary>
    Task<IReadOnlyList<PropertyAuthorityGrant>> GetUnrevokedGrantsForPropertyAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The latest completed platform sale of the property to this buyer that no ownership transfer has followed yet,
    /// or null.
    /// </summary>
    Task<Guid?> FindUntransferredSaleAsync(Guid propertyId, Guid buyerUserId, CancellationToken cancellationToken = default);

    /// <summary>Whether the property was sold through the platform and no ownership transfer has followed yet.</summary>
    Task<bool> IsAwaitingOwnershipTransferAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>Whether a sale or rental is still in force on the property at this moment (an overdue signature is not).</summary>
    Task<bool> HasCommitmentInForceAsync(Guid propertyId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>The property's ownership transfers, oldest first. Not tracked.</summary>
    Task<IReadOnlyList<PropertyOwnershipTransfer>> ListOwnershipTransfersAsync(Guid propertyId, CancellationToken cancellationToken = default);

    void AddOwnershipTransfer(PropertyOwnershipTransfer transfer);

    /// <summary>
    /// Every listing version of the property that was ever approved and published (now Approved or Superseded), with
    /// its review decision, oldest first. Not tracked.
    /// </summary>
    Task<IReadOnlyList<ListingSubmission>> ListPublishedVersionsAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>Grants of this user on this property that were never revoked, whether still valid or expired. Not tracked.</summary>
    Task<IReadOnlyList<PropertyAuthorityGrant>> GetUnrevokedGrantsAsync(
        Guid granteeUserId, Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>The grant, tracked, or null.</summary>
    Task<PropertyAuthorityGrant?> GetGrantAsync(Guid grantId, CancellationToken cancellationToken = default);

    /// <summary>Grants, newest first, filtered by property and/or grantee. Not tracked.</summary>
    Task<IReadOnlyList<PropertyAuthorityGrant>> ListGrantsAsync(
        Guid? propertyId, Guid? granteeUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The current (neither superseded nor removed) versions of these documents on requests by this user for this
    /// property. Not tracked.
    /// </summary>
    Task<IReadOnlyList<AuthorityDocumentEvidenceRow>> GetCurrentDocumentsAsync(
        Guid requesterUserId, Guid propertyId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default);

    /// <summary>These exact document versions, whatever their state. Not tracked.</summary>
    Task<IReadOnlyList<AuthorityDocumentEvidenceRow>> GetDocumentVersionsAsync(
        IReadOnlyCollection<Guid> versionIds, CancellationToken cancellationToken = default);

    /// <summary>Ids of versions waiting for a scan whose next attempt is due, oldest first.</summary>
    Task<IReadOnlyList<Guid>> ListDueScansAsync(DateTimeOffset now, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks one version waiting for a scan (skipping it when another worker holds it), runs <paramref name="work"/>
    /// on it, saves and commits. The work receives null when the version is locked elsewhere or no longer pending.
    /// Returns false when the save lost a concurrent change.
    /// </summary>
    Task<bool> ProcessLockedScanAsync(
        Guid versionId, Func<AuthorityRequestDocument?, Task> work, CancellationToken cancellationToken = default);

    void Add(AuthorityRequest request);

    void AddGrant(PropertyAuthorityGrant grant);

    /// <summary>
    /// Commits. New document versions and decisions on tracked requests are inserted. False when another change won:
    /// the request, a document or a grant changed concurrently, a second open request for the property, or a second
    /// grant in force for the same owner, property and purpose. Nothing is saved then.
    /// </summary>
    Task<bool> TrySaveAsync(CancellationToken cancellationToken = default);
}
