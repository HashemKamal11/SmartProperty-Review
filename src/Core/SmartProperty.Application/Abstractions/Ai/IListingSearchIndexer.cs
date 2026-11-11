using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Abstractions.Ai;

/// <summary>
/// Keeps the AI search service's copy of an advertisement in step with the marketplace. The service holds
/// advertisements, not properties: its external id for a record is the <b>listing</b> id.
/// </summary>
/// <remarks>
/// <para>
/// <b>Best effort, after the commit.</b> A publication, withdrawal or acceptance is decided and committed by the
/// database first; the index is told afterwards. An implementation never throws and never makes a committed decision
/// fail: if the service is down, switched off, or the property lacks something the service requires, the copy is simply
/// not updated. Nothing retries it in the background, so a missed update stays missed until the next publication or
/// withdrawal of that listing. This is a documented limitation, not hidden.
/// </para>
/// <para>
/// The service has no notion of currency, pricing period or availability dates, so it receives the listing's primary
/// price as a bare number. A search through it ranks and describes; the marketplace search remains the place for
/// price, period and availability filters.
/// </para>
/// </remarks>
public interface IListingSearchIndexer
{
    /// <summary>Adds or replaces the current visible advertisement of this purpose for the property.</summary>
    Task IndexAsync(Guid propertyId, ListingPurpose purpose, CancellationToken cancellationToken = default);

    /// <summary>Removes the advertisement with this listing id.</summary>
    Task RemoveAsync(Guid listingId, CancellationToken cancellationToken = default);
}
