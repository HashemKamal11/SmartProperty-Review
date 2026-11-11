using SmartProperty.Application.Marketplace.PublicListings;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

/// <summary>
/// The advertisements a visitor can see for each of a set of properties, for screens that list physical properties
/// (favorites, comparison) and need the price and terms that now live on listings.
/// </summary>
/// <remarks>
/// Same visibility as the public marketplace: only a listing that is published and open, whose approved version it
/// publishes, on an active property, and not held by a legacy reservation. A property with no visible listing has no
/// entry. A property can have at most one Sale and one Rent listing, so each list is short.
/// </remarks>
public interface IPropertyListingsLookup
{
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<PublicListingSummary>>> GetCurrentAsync(
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The visible advertisements with these listing ids, each with the property it advertises. A listing that is not
    /// visible (unpublished, withdrawn, closed, unapproved) has no entry, so a stale record in the AI service for a
    /// listing that is gone maps to nothing.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, PropertyListingReference>> GetByListingIdsAsync(
        IReadOnlyCollection<Guid> listingIds,
        CancellationToken cancellationToken = default);
}

/// <summary>A visible advertisement and the property it advertises.</summary>
public sealed record PropertyListingReference(Guid PropertyId, PublicListingSummary Listing);
