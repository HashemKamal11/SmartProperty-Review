namespace SmartProperty.Application.Marketplace.PublicListings;

/// <summary>
/// The search filters of a public listing query after validation: trimmed, blank values turned into absent ones,
/// country and currency upper-cased, and the sort order resolved.
/// </summary>
/// <remarks>
/// Deliberately independent of the Marketplace Domain: it carries only plain values. The listing purpose and the
/// property type are not part of it, because their types belong to the Domain and need no validation beyond
/// binding.
/// </remarks>
internal sealed record PublicListingSearchFilters(
    string? CountryCode,
    string? City,
    string? Region,
    string? District,
    decimal? MinPrice,
    decimal? MaxPrice,
    string? Currency,
    PublicListingSortOrder Sort);
