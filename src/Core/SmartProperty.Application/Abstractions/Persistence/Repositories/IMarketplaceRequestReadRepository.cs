using SmartProperty.Application.Marketplace.Requests;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

/// <summary>
/// Reads of requests and commitments, always scoped to one person in the database itself.
/// </summary>
/// <remarks>
/// A customer sees only their own requests. An advertiser sees only the requests made against a version they
/// authored. A commitment is visible to its customer and to its owner and to no one else. Another person's row and a
/// missing one are the same <see langword="null"/>. Scoping, counting, ordering and paging happen in the database.
/// </remarks>
public interface IMarketplaceRequestReadRepository
{
    Task<RequestPage> ListCustomerRequestsAsync(
        Guid customerUserId,
        RequestKind? kind,
        RequestStatusFilter? status,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    Task<MarketplaceRequestView?> GetCustomerRequestAsync(
        Guid customerUserId,
        RequestKind kind,
        Guid requestId,
        CancellationToken cancellationToken = default);

    Task<RequestPage> ListReceivedRequestsAsync(
        Guid ownerUserId,
        RequestKind? kind,
        Guid? listingId,
        RequestStatusFilter? status,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    Task<MarketplaceRequestView?> GetReceivedRequestAsync(
        Guid ownerUserId,
        RequestKind kind,
        Guid requestId,
        CancellationToken cancellationToken = default);

    Task<CommitmentPage> ListCommitmentsAsync(
        DateTimeOffset at,
        Guid userId,
        CommitmentRole role,
        CommitmentStatus? status,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    Task<CommitmentView?> GetCommitmentAsync(DateTimeOffset at, Guid userId, Guid commitmentId, CancellationToken cancellationToken = default);
}
