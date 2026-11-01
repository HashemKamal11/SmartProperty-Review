using SmartProperty.Application.Marketplace.PublicListings;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

/// <summary>
/// Read side of the public marketplace. Implemented in Persistence; every rule below is enforced in the query
/// itself, never by the caller.
/// </summary>
/// <remarks>
/// A listing is visible only when it is Published and Open, its property is Active, and its published pointer
/// refers to an Approved submission of that same listing. All displayed content comes from that one published
/// snapshot: never from a newer draft, pending, rejected or merely approved-but-unpublished submission, and never
/// from the live Registry address or type. Internal identifiers may be used to join and filter, but they are never
/// projected: the results carry no property id, submission id, version, status, owner, evidence, review or
/// Workspace data and no street address, postal code or coordinates. Filtering, counting, ordering and paging all
/// happen in the database, with a deterministic order that ends in the listing id.
/// </remarks>
public interface IPublicListingReadRepository
{
    /// <summary>
    /// One page of visible listings matching <paramref name="criteria"/>, plus the total number of matches.
    /// </summary>
    Task<PublicListingPage> SearchAsync(
        PublicListingSearchCriteria criteria,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The visible listing with the given id, or <see langword="null"/> for every listing that cannot be shown
    /// publicly, whatever the reason.
    /// </summary>
    Task<PublicListingDetail?> GetByIdAsync(
        Guid listingId,
        CancellationToken cancellationToken = default);
}
