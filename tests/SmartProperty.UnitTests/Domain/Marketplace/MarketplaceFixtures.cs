using SmartProperty.Domain.Marketplace;

namespace SmartProperty.UnitTests.Domain.Marketplace;

internal static class MarketplaceFixtures
{
    internal static readonly DateTimeOffset CreatedAt = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset PublishedAt = CreatedAt.AddHours(1);
    internal static readonly DateTimeOffset RequestedAt = PublishedAt.AddHours(1);
    internal static readonly DateTimeOffset DecidedAt = RequestedAt.AddHours(1);

    internal static PropertyListing CreateListing(
        ListingPurpose purpose,
        Guid? propertyId = null,
        Guid? listingId = null,
        bool publish = true)
    {
        var listing = new PropertyListing(
            listingId ?? Guid.NewGuid(),
            propertyId ?? Guid.NewGuid(),
            purpose,
            CreatedAt);

        if (publish)
        {
            listing.Publish(Guid.NewGuid(), PublishedAt);
        }

        return listing;
    }

    internal static PurchaseRequest CreatePurchaseRequest(PropertyListing listing, Guid? id = null)
    {
        return new PurchaseRequest(id ?? Guid.NewGuid(), listing, Guid.NewGuid(), RequestedAt);
    }

    internal static RentalRequest CreateRentalRequest(PropertyListing listing, Guid? id = null)
    {
        return new RentalRequest(id ?? Guid.NewGuid(), listing, Guid.NewGuid(), RequestedAt);
    }
}
