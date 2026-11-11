using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.PropertyRegistry.Ai;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>
/// Builds what the AI search service holds for an advertisement, or says what stops it from being sent.
/// </summary>
public static class ListingAiMapper
{
    /// <summary>The service refuses more than this many bedrooms or bathrooms.</summary>
    public const int MaxRoomCount = 50;

    /// <summary>What prevents the advertisement from being sent, in plain words. Empty when it can be.</summary>
    public static IReadOnlyList<string> Blockers(Property property, PublicListingDetail listing)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(listing);

        var blockers = new List<string>();
        var missing = new List<string>();

        if (property.Area is null)
        {
            missing.Add("area");
        }

        if (property.Physical.AreaSqm is null)
        {
            missing.Add("area in square metres");
        }

        if (property.Bedrooms is null)
        {
            missing.Add("bedrooms");
        }

        if (property.Bathrooms is null)
        {
            missing.Add("bathrooms");
        }

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

        if (listing.Price.Amount <= 0m)
        {
            blockers.Add("no price");
        }

        return blockers;
    }

    /// <summary>The advertisement for a property and listing that have no <see cref="Blockers"/>.</summary>
    public static AiListing ToListing(Property property, PublicListingDetail listing)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(listing);

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

        // A listing carries one title and one description in the language its advertiser wrote them. The service has an
        // Arabic and an English field and searches both, so the same text goes into both rather than being guessed at.
        return new AiListing(
            property.Area!.AiId,
            property.Compound?.AiId,
            AiVocabulary.ToAi(property.Type),
            AiVocabulary.ToAi(listing.Purpose),
            listing.Price.Amount,
            property.Physical.AreaSqm!.Value,
            property.Bedrooms!.Value,
            property.Bathrooms!.Value,
            property.Physical.Floor,
            property.FinishingStatus is { } finishing ? AiVocabulary.ToAi(finishing) : null,
            property.Physical.Furnished,
            view,
            amenities,
            "Available",
            listing.Title,
            listing.Title,
            listing.Description,
            listing.Description);
    }
}
