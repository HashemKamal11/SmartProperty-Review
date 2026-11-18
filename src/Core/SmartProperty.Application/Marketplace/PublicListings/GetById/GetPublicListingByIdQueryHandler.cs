using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.PublicListings.GetById;

/// <summary>
/// One public listing for its detail page: the published listing, the property's public facts read live from the
/// registry, and the property's other visible listings (so a page can switch between its Sale and Rent offers).
/// </summary>
public sealed class GetPublicListingByIdQueryHandler : IQueryHandler<GetPublicListingByIdQuery, PublicListingDetail>
{
    private readonly IPublicListingReadRepository publicListings;
    private readonly IPropertyRepository? properties;
    private readonly IPropertyListingsLookup? listingsLookup;

    /// <summary>The listing alone, without the property's facts or its other listings.</summary>
    public GetPublicListingByIdQueryHandler(IPublicListingReadRepository publicListings)
    {
        this.publicListings = publicListings;
    }

    public GetPublicListingByIdQueryHandler(
        IPublicListingReadRepository publicListings,
        IPropertyRepository properties,
        IPropertyListingsLookup listingsLookup)
    {
        this.publicListings = publicListings;
        this.properties = properties;
        this.listingsLookup = listingsLookup;
    }

    public async Task<Result<PublicListingDetail>> Handle(
        GetPublicListingByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // No listing can have the empty id, so asking the database would only confirm what is already known.
        if (query.ListingId == Guid.Empty)
        {
            return Result<PublicListingDetail>.Failure(PublicListingErrors.NotFound);
        }

        var listing = query.ViewerUserId is { } viewer && viewer != Guid.Empty
            ? await publicListings.GetByIdAsync(query.ListingId, viewer, cancellationToken)
            : await publicListings.GetByIdAsync(query.ListingId, cancellationToken);

        // One answer for every listing that cannot be shown publicly: the caller cannot tell a missing listing
        // from an unpublished, withdrawn, reserved or unapproved one.
        if (listing is null)
        {
            return Result<PublicListingDetail>.Failure(PublicListingErrors.NotFound);
        }

        if (properties is null || listingsLookup is null || listing.PropertyId == Guid.Empty)
        {
            return Result<PublicListingDetail>.Success(listing);
        }

        // The listing is visible, so its property is active; only its public facts are copied.
        var property = await properties.GetDetailsByIdAsync(listing.PropertyId, cancellationToken);
        var current = await listingsLookup.GetCurrentAsync([listing.PropertyId], cancellationToken);
        var others = current.TryGetValue(listing.PropertyId, out var all)
            ? all.Where(other => other.ListingId != listing.ListingId).ToArray()
            : [];

        return Result<PublicListingDetail>.Success(listing with
        {
            Property = property is null ? null : FactsOf(property),
            OtherListings = others,
        });
    }

    private static PublicPropertyFacts FactsOf(PropertyDetails property) =>
        new(
            (property.Images ?? []).OrderBy(image => image.SortOrder).Select(image => image.Id).ToArray(),
            (property.Amenities ?? []).Select(item => new PublicNamedItem(item.Code, item.NameAr, item.NameEn)).ToArray(),
            (property.Views ?? []).Select(item => new PublicNamedItem(item.Code, item.NameAr, item.NameEn)).ToArray(),
            property.BuiltDate,
            property.FinishingStatus,
            property.Physical?.Floor,
            property.Physical?.Furnished,
            property.Place is { } place
                ? new PublicPlace(
                    new PublicPlaceLevel(place.Country.Id, place.Country.NameEn, place.Country.NameAr),
                    new PublicPlaceLevel(place.City.Id, place.City.NameEn, place.City.NameAr),
                    new PublicPlaceLevel(place.Area.Id, place.Area.NameEn, place.Area.NameAr),
                    place.Compound is { } compound
                        ? new PublicPlaceLevel(compound.Id, compound.NameEn, compound.NameAr)
                        : null)
                : null,
            PublicMapPoint.Approximate(property.Address.Latitude, property.Address.Longitude));
}
