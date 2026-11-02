using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Ai;

/// <summary>
/// Builds what the AI search service holds for a property, or says what stops the property from being sent.
/// </summary>
internal static class AiListingMapper
{
    /// <summary>The service refuses more than this many bedrooms or bathrooms.</summary>
    public const int MaxRoomCount = 50;

    /// <summary>
    /// What prevents the property from being sent, in plain words. Empty when it can be: the service requires a
    /// transaction type, price, area, place, and room counts, and limits the room counts it accepts.
    /// </summary>
    public static IReadOnlyList<string> Blockers(Property property)
    {
        var blockers = new List<string>();

        var missing = property.MissingForAiSync();
        if (missing.Count > 0)
        {
            blockers.Add($"missing {string.Join(", ", missing)}");
        }

        if (property.Bedrooms > MaxRoomCount)
        {
            blockers.Add($"bedrooms exceeds the AI limit of {MaxRoomCount}");
        }

        if (property.Bathrooms > MaxRoomCount)
        {
            blockers.Add($"bathrooms exceeds the AI limit of {MaxRoomCount}");
        }

        return blockers;
    }

    /// <summary>The listing for a property that has no <see cref="Blockers"/>.</summary>
    public static AiListing ToListing(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);

        var listing = property.Listing;

        // The service takes one view; the first in display order is the most prominent.
        var view = property.Views
            .OrderBy(candidate => candidate.SortOrder)
            .Select(candidate => candidate.Code)
            .FirstOrDefault();

        // Several of this platform's amenities can share one AI code (a private and a shared gym are both "gym").
        var amenities = property.Amenities
            .OrderBy(amenity => amenity.SortOrder)
            .Select(amenity => amenity.AiCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new AiListing(
            property.Area!.AiId,
            property.Compound?.AiId,
            AiVocabulary.ToAi(property.Type),
            AiVocabulary.ToAi(listing.TransactionType!.Value),
            listing.Price!.Value,
            listing.AreaSqm!.Value,
            property.Bedrooms!.Value,
            property.Bathrooms!.Value,
            listing.Floor,
            property.FinishingStatus is { } finishing ? AiVocabulary.ToAi(finishing) : null,
            listing.Furnished,
            view,
            amenities,
            AiVocabulary.ToAi(listing.ListingStatus),
            listing.TitleAr,
            listing.TitleEn,
            listing.DescriptionAr,
            listing.DescriptionEn);
    }
}
