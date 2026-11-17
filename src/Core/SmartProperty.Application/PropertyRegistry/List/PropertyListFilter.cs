using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.List;

/// <summary>
/// Already-validated and normalized filters for one registry page. Every null (or empty) member means "no
/// restriction"; every non-null member must hold at once.
/// </summary>
/// <param name="Types">Any of these property types. Null or empty means every type.</param>
/// <param name="Status">One registry status, or null for Active and Archived alike.</param>
/// <param name="CountryCode">An upper-case two-letter code, or null.</param>
/// <param name="District">
/// Trimmed district name, or null. Matches the property's area by its English or Arabic name (case-insensitively),
/// or, for a property without an area, the district stored on its address.
/// </param>
/// <param name="AreaId">The registry area (place) id, or null.</param>
/// <param name="MinBedrooms">Inclusive lower bound on bedrooms, or null.</param>
/// <param name="MaxBedrooms">Inclusive upper bound on bedrooms, or null.</param>
/// <param name="MinMatchPercentage">
/// Keeps only properties whose latest stored match for <paramref name="MatchUserId"/> is at least this percentage.
/// </param>
/// <param name="MatchUserId">
/// The signed-in user whose stored matches supply the percentage. When set, each returned property carries that
/// user's latest match percentage (or null when there is none).
/// </param>
/// <param name="SearchTerms">
/// Free-text terms. Every term must appear (case-insensitively) in at least one of the city, region, district or
/// address line stored on the address, or the English or Arabic name of the property's area, city, country or
/// compound. Null or empty means no text restriction.
/// </param>
/// <param name="Sort">How the page is ordered.</param>
/// <param name="OwnerUserId">Only properties of this owner, or null for every owner.</param>
/// <param name="CityId">The city of the property's area, or null.</param>
/// <param name="CompoundId">The compound, or null.</param>
/// <param name="MinBathrooms">Inclusive lower bound on bathrooms, or null.</param>
/// <param name="MaxBathrooms">Inclusive upper bound on bathrooms, or null.</param>
/// <param name="Furnished">Only furnished (true) or unfurnished (false) properties, or null for either.</param>
/// <param name="FinishingStatuses">Any of these finishing statuses. Null or empty means any.</param>
/// <param name="MinAreaSqm">Inclusive lower bound on the built-up area in square metres, or null.</param>
/// <param name="MaxAreaSqm">Inclusive upper bound on the built-up area in square metres, or null.</param>
public sealed record PropertyListFilter(
    IReadOnlyList<PropertyType>? Types = null,
    PropertyStatus? Status = null,
    string? CountryCode = null,
    string? District = null,
    Guid? AreaId = null,
    int? MinBedrooms = null,
    int? MaxBedrooms = null,
    decimal? MinMatchPercentage = null,
    Guid? MatchUserId = null,
    IReadOnlyList<string>? SearchTerms = null,
    PropertyListSortOrder Sort = PropertyListSortOrder.Relevance,
    Guid? OwnerUserId = null,
    Guid? CityId = null,
    Guid? CompoundId = null,
    int? MinBathrooms = null,
    int? MaxBathrooms = null,
    bool? Furnished = null,
    IReadOnlyList<FinishingStatus>? FinishingStatuses = null,
    decimal? MinAreaSqm = null,
    decimal? MaxAreaSqm = null);
