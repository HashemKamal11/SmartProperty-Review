using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.List;

/// <summary>
/// Already-validated and normalized filters for one registry page. Every null (or empty) member means "no
/// restriction"; every non-null member must hold at once.
/// </summary>
/// <param name="Types">Any of these property types. Null or empty means every type.</param>
/// <param name="Status">One status, or null for Active and Archived alike.</param>
/// <param name="CountryCode">An upper-case two-letter code, or null.</param>
/// <param name="District">Trimmed district name, compared case-insensitively, or null.</param>
/// <param name="AreaId">The registry area (place) id, or null.</param>
/// <param name="MinPrice">Inclusive lower bound on the listing price, or null.</param>
/// <param name="MaxPrice">Inclusive upper bound on the listing price, or null.</param>
/// <param name="MinBedrooms">Inclusive lower bound on bedrooms, or null.</param>
/// <param name="MaxBedrooms">Inclusive upper bound on bedrooms, or null.</param>
/// <param name="MinMatchPercentage">
/// Keeps only properties whose latest stored match for <paramref name="MatchUserId"/> is at least this percentage.
/// </param>
/// <param name="MatchUserId">
/// The signed-in user whose stored matches supply the percentage. When set, each returned property carries that
/// user's latest match percentage (or null when there is none).
/// </param>
public sealed record PropertyListFilter(
    IReadOnlyList<PropertyType>? Types = null,
    PropertyStatus? Status = null,
    string? CountryCode = null,
    string? District = null,
    Guid? AreaId = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    int? MinBedrooms = null,
    int? MaxBedrooms = null,
    decimal? MinMatchPercentage = null,
    Guid? MatchUserId = null);
