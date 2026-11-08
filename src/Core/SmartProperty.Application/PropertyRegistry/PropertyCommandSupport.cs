using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Locations;
using SmartProperty.Application.PropertyImages;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Locations;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry;

/// <summary>
/// The pieces every command that mutates a property shares, so create, update, and archive validate, stamp, and
/// report a change the same way.
/// </summary>
internal static class PropertyCommandSupport
{
    /// <summary>
    /// Parses an enum by its exact name. A number, different casing, or surrounding whitespace is not a name, so
    /// every spelling a client can send has one meaning.
    /// </summary>
    public static bool TryParseName<TEnum>(string? value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        parsed = default;

        if (value is null || !Enum.GetNames<TEnum>().Contains(value, StringComparer.Ordinal))
        {
            return false;
        }

        parsed = Enum.Parse<TEnum>(value);

        return true;
    }

    public static string NamesOf<TEnum>()
        where TEnum : struct, Enum
    {
        return string.Join(", ", Enum.GetNames<TEnum>());
    }

    /// <summary>
    /// Validates the optional specification fields shared by create and update, returning the validation
    /// message or null. A null finishing status is valid and means "not stated".
    /// </summary>
    public static string? ValidateSpecifications(
        int? bedrooms,
        int? bathrooms,
        DateOnly? builtDate,
        string? finishingStatus,
        out FinishingStatus? parsedFinishingStatus)
    {
        parsedFinishingStatus = null;

        if (bedrooms is < 0 or > Property.MaxRoomCount)
        {
            return $"Bedrooms must be between 0 and {Property.MaxRoomCount}.";
        }

        if (bathrooms is < 0 or > Property.MaxRoomCount)
        {
            return $"Bathrooms must be between 0 and {Property.MaxRoomCount}.";
        }

        if (builtDate < Property.EarliestBuiltDate)
        {
            return $"Built date must not be earlier than {Property.EarliestBuiltDate:yyyy-MM-dd}.";
        }

        if (finishingStatus is not null)
        {
            if (!TryParseName<FinishingStatus>(finishingStatus, out var parsed))
            {
                return $"Finishing status must be one of: {NamesOf<FinishingStatus>()}.";
            }

            parsedFinishingStatus = parsed;
        }

        return null;
    }

    /// <summary>
    /// Validates the market-facing fields shared by create and update and builds them, returning the validation
    /// message or null. Every field is optional; a missing listing status means Available.
    /// </summary>
    public static string? ValidateListing(
        string? transactionType,
        decimal? price,
        decimal? areaSqm,
        int? floor,
        bool? furnished,
        string? listingStatus,
        string? titleAr,
        string? titleEn,
        string? descriptionAr,
        string? descriptionEn,
        out PropertyListingInfo listing)
    {
        listing = PropertyListingInfo.Empty;

        TransactionType? parsedTransaction = null;
        if (transactionType is not null)
        {
            if (!TryParseName<TransactionType>(transactionType, out var transaction))
            {
                return $"Transaction type must be one of: {NamesOf<TransactionType>()}.";
            }

            parsedTransaction = transaction;
        }

        var status = ListingStatus.Available;
        if (listingStatus is not null)
        {
            if (!TryParseName<ListingStatus>(listingStatus, out status))
            {
                return $"Listing status must be one of: {NamesOf<ListingStatus>()}.";
            }
        }

        if (price is <= 0m)
        {
            return "Price must be greater than zero.";
        }

        if (areaSqm is <= 0m or > PropertyListingInfo.MaxAreaSqm)
        {
            return $"Area must be greater than zero and at most {PropertyListingInfo.MaxAreaSqm} square metres.";
        }

        if (floor is < PropertyListingInfo.MinFloor or > PropertyListingInfo.MaxFloor)
        {
            return $"Floor must be between {PropertyListingInfo.MinFloor} and {PropertyListingInfo.MaxFloor}.";
        }

        if (TooLong(titleAr, PropertyListingInfo.MaxTitleLength) || TooLong(titleEn, PropertyListingInfo.MaxTitleLength))
        {
            return $"A title must not exceed {PropertyListingInfo.MaxTitleLength} characters.";
        }

        if (TooLong(descriptionAr, PropertyListingInfo.MaxDescriptionLength)
            || TooLong(descriptionEn, PropertyListingInfo.MaxDescriptionLength))
        {
            return $"A description must not exceed {PropertyListingInfo.MaxDescriptionLength} characters.";
        }

        listing = new PropertyListingInfo(
            parsedTransaction,
            price,
            areaSqm,
            floor,
            furnished,
            status,
            titleAr,
            titleEn,
            descriptionAr,
            descriptionEn);

        return null;
    }

    private static bool TooLong(string? value, int maxLength)
    {
        return value is not null && value.Trim().Length > maxLength;
    }

    /// <summary>
    /// The instant a real change is stamped with: the clock, read once and truncated to the microsecond precision
    /// PostgreSQL stores, so the value returned is exactly the value persisted and later read back. A clock behind
    /// the current update instant keeps that instant rather than moving backwards.
    /// </summary>
    public static DateTimeOffset ResolveUpdatedAt(
        IDateTimeProvider dateTimeProvider,
        DateTimeOffset currentUpdatedAt)
    {
        var now = dateTimeProvider.UtcNow;
        var truncated = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMicrosecond), now.Offset);

        return truncated >= currentUpdatedAt ? truncated : currentUpdatedAt;
    }

    public static List<AmenityDetails> ToAmenityDetails(IEnumerable<Amenity> amenities)
    {
        return amenities
            .OrderBy(amenity => amenity.SortOrder)
            .Select(amenity => new AmenityDetails(amenity.Code, amenity.NameAr, amenity.NameEn))
            .ToList();
    }

    public static List<ViewDetails> ToViewDetails(IEnumerable<PropertyView> views)
    {
        return views
            .OrderBy(view => view.SortOrder)
            .Select(view => new ViewDetails(view.Code, view.NameAr, view.NameEn))
            .ToList();
    }

    public static PropertyListingDetails ToListingDetails(PropertyListingInfo listing)
    {
        return new PropertyListingDetails(
            listing.TransactionType,
            listing.Price,
            listing.AreaSqm,
            listing.Floor,
            listing.Furnished,
            listing.ListingStatus,
            listing.TitleAr,
            listing.TitleEn,
            listing.DescriptionAr,
            listing.DescriptionEn);
    }

    /// <summary>
    /// Turns the place a client chose into the tracked area and compound, shared by create and update. No place at
    /// all is valid; a compound without its area, an unknown id, or a compound from another area is not.
    /// </summary>
    public static async Task<Result<(Area? Area, Compound? Compound)>> ResolvePlaceAsync(
        ILocationRepository locationRepository,
        Guid? areaId,
        Guid? compoundId,
        Func<string, Error> validationFailed,
        CancellationToken cancellationToken)
    {
        if (areaId is null && compoundId is null)
        {
            return Result<(Area?, Compound?)>.Success((null, null));
        }

        if (areaId is null)
        {
            return Result<(Area?, Compound?)>.Failure(
                validationFailed("A compound can only be chosen together with its area."));
        }

        var area = await locationRepository.GetAreaAsync(areaId.Value, cancellationToken);
        if (area is null)
        {
            return Result<(Area?, Compound?)>.Failure(validationFailed("Unknown area."));
        }

        if (compoundId is null)
        {
            return Result<(Area?, Compound?)>.Success((area, null));
        }

        var compound = await locationRepository.GetCompoundAsync(compoundId.Value, cancellationToken);
        if (compound is null)
        {
            return Result<(Area?, Compound?)>.Failure(validationFailed("Unknown compound."));
        }

        if (compound.AreaId != area.Id)
        {
            return Result<(Area?, Compound?)>.Failure(validationFailed("The compound does not belong to the area."));
        }

        return Result<(Area?, Compound?)>.Success((area, compound));
    }

    /// <summary>
    /// The address of a property placed in an area: country code, city and district come from the area, and only the
    /// street line, postal code and coordinates are the caller's own. Region is not a level of the places, so it stays empty.
    /// </summary>
    public static Result<PropertyAddress> BuildAddress(
        Area area,
        string? addressLine,
        string? postalCode,
        decimal? latitude,
        decimal? longitude,
        Func<string, Error> validationFailed)
    {
        var country = area.City.Country;

        if (country.Code is null)
        {
            return Result<PropertyAddress>.Failure(validationFailed(
                $"The country '{country.NameEn}' has no country code yet, so a property cannot be placed in it."));
        }

        return Result<PropertyAddress>.Success(new PropertyAddress(
            country.Code,
            area.City.NameEn,
            region: null,
            area.NameEn,
            addressLine,
            postalCode,
            latitude,
            longitude));
    }

    /// <summary>The place of a tracked property whose area, city, and country are loaded, or null without an area.</summary>
    public static PropertyPlaceDetails? ToPlaceDetails(Property property)
    {
        if (property.Area is not { } area)
        {
            return null;
        }

        return new PropertyPlaceDetails(
            new LocationDetails(area.City.Country.Id, area.City.Country.NameEn, area.City.Country.NameAr),
            new LocationDetails(area.City.Id, area.City.NameEn, area.City.NameAr),
            new LocationDetails(area.Id, area.NameEn, area.NameAr),
            property.Compound is { } compound
                ? new LocationDetails(compound.Id, compound.NameEn, compound.NameAr)
                : null);
    }

    public static AiSyncDetails ToAiSyncDetails(Property property)
    {
        return new AiSyncDetails(property.AiSyncStatus, property.AiSyncedAt, property.AiSyncError);
    }

    /// <summary>The read model of the tracked aggregate, as the command left it.</summary>
    public static PropertyDetails ToDetails(Property property)
    {
        return new PropertyDetails(
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
            ToAmenityDetails(property.Amenities),
            ToViewDetails(property.Views),
            ToListingDetails(property.Listing),
            ToAiSyncDetails(property),
            ToPlaceDetails(property),
            ToImageDetails(property.Images));
    }

    public static List<PropertyImageDetails> ToImageDetails(IEnumerable<PropertyImage> images)
    {
        return images
            .OrderBy(image => image.SortOrder)
            .ThenBy(image => image.CreatedAt)
            .Select(image => new PropertyImageDetails(
                image.Id, image.FileName, image.ContentType, image.SizeBytes, image.SortOrder))
            .ToList();
    }
}
