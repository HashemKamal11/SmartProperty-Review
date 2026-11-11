using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

/// <summary>
/// The write side of purchase and rental requests and of the commitments they become. It extends the listing write
/// repository, so one unit of work has one transaction and one lock order: property, then the advertiser's grant for
/// the purpose, then the listing rows, then the requests, then the commitments.
/// </summary>
/// <remarks>
/// <para>
/// A request or commitment found <b>before</b> the property is locked is read without tracking and only names the
/// property to lock. After the lock, <c>Get...</c> returns the tracked, locked row, and that read is the one every
/// decision uses. The property lock serializes every writer of one property, so a decision is never made over a
/// snapshot that another request changed.
/// </para>
/// <para>
/// The database still enforces the rules that matter most when handlers are wrong or racing: no two active rentals of
/// a property overlap, a property has at most one active sale, and a customer has one open request per listing. A
/// lost race on any of them is reported by <c>SaveChangesAsync</c> as a <c>ListingWriteConflictException</c>.
/// </para>
/// </remarks>
public interface IMarketplaceRequestWriteRepository : IListingSubmissionWriteRepository
{
    Task<PurchaseRequest?> FindPurchaseRequestAsync(Guid requestId, CancellationToken cancellationToken = default);

    Task<RentalRequest?> FindRentalRequestAsync(Guid requestId, CancellationToken cancellationToken = default);

    Task<PropertyCommitment?> FindCommitmentAsync(Guid commitmentId, CancellationToken cancellationToken = default);

    /// <summary>The request, locked and tracked. The property lock must be held.</summary>
    Task<PurchaseRequest?> GetPurchaseRequestAsync(Guid requestId, CancellationToken cancellationToken = default);

    Task<RentalRequest?> GetRentalRequestAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>The commitment, locked and tracked. The property lock must be held.</summary>
    Task<PropertyCommitment?> GetCommitmentAsync(Guid commitmentId, CancellationToken cancellationToken = default);

    /// <summary>Every purchase request of the property that is still waiting, locked and tracked.</summary>
    Task<IReadOnlyList<PurchaseRequest>> GetOpenPurchaseRequestsAsync(Guid propertyId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RentalRequest>> GetOpenRentalRequestsAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every commitment of the property that has not finished (pending signature, signed or occupied), locked and
    /// tracked, including ones whose signing deadline has passed: the caller expires those in the same unit of work.
    /// </summary>
    Task<IReadOnlyList<PropertyCommitment>> GetOpenCommitmentsAsync(Guid propertyId, CancellationToken cancellationToken = default);

    Task AddPurchaseRequestAsync(PurchaseRequest request, CancellationToken cancellationToken = default);

    Task AddRentalRequestAsync(RentalRequest request, CancellationToken cancellationToken = default);

    Task AddCommitmentAsync(PropertyCommitment commitment, CancellationToken cancellationToken = default);
}
