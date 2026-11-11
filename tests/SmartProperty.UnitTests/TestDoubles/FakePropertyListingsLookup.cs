using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.PublicListings;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>The visible advertisements, as a test scripts them: by listing id and by property id.</summary>
internal sealed class FakePropertyListingsLookup : IPropertyListingsLookup
{
    public Dictionary<Guid, PropertyListingReference> ByListing { get; } = [];

    public Dictionary<Guid, IReadOnlyList<PublicListingSummary>> ByProperty { get; } = [];

    /// <summary>Makes a listing visible for a property and returns its id.</summary>
    public Guid Show(Guid propertyId, ListingPurpose purpose = ListingPurpose.Sale)
    {
        var listingId = Guid.NewGuid();
        var summary = new PublicListingSummary(
            listingId,
            purpose,
            "A title",
            new PublicListingPrice(1_000m, "EGP", purpose == ListingPurpose.Rent ? RentPeriod.Monthly : null),
            PropertyType.Apartment,
            new PublicListingLocation("EG", "Cairo", "Cairo", "Maadi"),
            DateTimeOffset.UtcNow);
        ByListing[listingId] = new PropertyListingReference(propertyId, summary);
        ByProperty[propertyId] = [.. ByProperty.GetValueOrDefault(propertyId) ?? [], summary];

        return listingId;
    }

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<PublicListingSummary>>> GetCurrentAsync(
        IReadOnlyCollection<Guid> propertyIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<PublicListingSummary>>>(
            ByProperty.Where(pair => propertyIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));

    public Task<IReadOnlyDictionary<Guid, PropertyListingReference>> GetByListingIdsAsync(
        IReadOnlyCollection<Guid> listingIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, PropertyListingReference>>(
            ByListing.Where(pair => listingIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));
}
