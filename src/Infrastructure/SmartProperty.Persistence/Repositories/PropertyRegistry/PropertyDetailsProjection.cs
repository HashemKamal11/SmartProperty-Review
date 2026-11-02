using System.Linq.Expressions;
using SmartProperty.Application.Locations;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Persistence.Repositories.PropertyRegistry;

/// <summary>
/// The one definition of how a property becomes its read model, projected in the database. The property endpoints and
/// anything else that returns a property (a user's favorites, for one) use it, so every one of them returns exactly the
/// same shape and no aggregate is ever materialized to answer a read.
/// </summary>
internal static class PropertyDetailsProjection
{
    public static readonly Expression<Func<Property, PropertyDetails>> Expression = property => new PropertyDetails(
            property.Id,
            property.Type,
            property.Status,
            new PropertyAddressDetails(
                property.Address.CountryCode,
                property.Address.City,
                property.Address.Region,
                property.Address.District,
                property.Address.AddressLine,
                property.Address.PostalCode,
                property.Address.Latitude,
                property.Address.Longitude),
            property.CreatedAt,
            property.UpdatedAt,
            property.Bedrooms,
            property.Bathrooms,
            property.BuiltDate,
            property.FinishingStatus,
            property.Amenities
                .OrderBy(amenity => amenity.SortOrder)
                .Select(amenity => new AmenityDetails(amenity.Code, amenity.NameAr, amenity.NameEn))
                .ToList(),
            property.Views
                .OrderBy(view => view.SortOrder)
                .Select(view => new ViewDetails(view.Code, view.NameAr, view.NameEn))
                .ToList(),
            new PropertyListingDetails(
                property.Listing.TransactionType,
                property.Listing.Price,
                property.Listing.AreaSqm,
                property.Listing.Floor,
                property.Listing.Furnished,
                property.Listing.ListingStatus,
                property.Listing.TitleAr,
                property.Listing.TitleEn,
                property.Listing.DescriptionAr,
                property.Listing.DescriptionEn),
            new AiSyncDetails(property.AiSyncStatus, property.AiSyncedAt, property.AiSyncError),
            property.Area == null
                ? null
                : new PropertyPlaceDetails(
                    new LocationDetails(property.Area.City.Country.Id, property.Area.City.Country.NameEn, property.Area.City.Country.NameAr),
                    new LocationDetails(property.Area.City.Id, property.Area.City.NameEn, property.Area.City.NameAr),
                    new LocationDetails(property.Area.Id, property.Area.NameEn, property.Area.NameAr),
                    property.Compound == null
                        ? null
                        : new LocationDetails(property.Compound.Id, property.Compound.NameEn, property.Compound.NameAr)));
}
