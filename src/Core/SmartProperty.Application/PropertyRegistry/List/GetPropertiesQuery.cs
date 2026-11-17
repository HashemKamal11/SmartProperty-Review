using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.List;

/// <summary>
/// Reads the canonical property registry.
/// </summary>
/// <remarks>
/// Exact-match and range filters plus the standard page parameters. It is not a general query language — no
/// free-text search, no sort expression, no field selection.
/// </remarks>
/// <param name="Types">Restrict to any of these property types. Null or empty means every type.</param>
/// <param name="Status">
/// Restrict to one status. Null means every status: Active and Archived properties are listed together, and each
/// item carries its own status, so a caller filters with <see cref="PropertyStatus.Active"/> or
/// <see cref="PropertyStatus.Archived"/> when it wants only one.
/// </param>
/// <param name="CountryCode">
/// Restrict to one two-letter country code. Null, empty, or whitespace means every country. Any other value is
/// trimmed, must then be exactly two ASCII letters, and is normalized to uppercase before the exact comparison.
/// Whether the code is an assigned country is not checked.
/// </param>
/// <param name="Page">
/// One-based page number. Null means page 1; a value below 1 is clamped to 1 and a value above
/// <see cref="PageParameters.MaxPageNumber"/> is clamped to it, exactly as <see cref="PageParameters"/> does.
/// </param>
/// <param name="PageSize">
/// Page size. Null means 10; a value below 1 is clamped to 1 and a value above
/// <see cref="PageParameters.MaxPageSize"/> is clamped to it, exactly as <see cref="PageParameters"/> does.
/// </param>
/// <param name="District">
/// Restrict to one district: the English or Arabic name of the property's area (case-insensitive). Null or
/// whitespace means any.
/// </param>
/// <param name="AreaId">Restrict to one registry area. Null means any.</param>
/// <param name="MinBedrooms">Inclusive lower bound on bedrooms (use it alone for "5+").</param>
/// <param name="MaxBedrooms">Inclusive upper bound on bedrooms (equal to <paramref name="MinBedrooms"/> for an exact count).</param>
/// <param name="MinMatchPercentage">
/// 0–100. Keeps only properties whose latest stored match for the signed-in user is at least this value. Requires
/// <paramref name="MatchUserId"/>.
/// </param>
/// <param name="MatchUserId">The signed-in user, or null for an anonymous caller.</param>
/// <param name="Search">
/// Free text, at most 100 characters. Split on whitespace into at most 5 distinct terms; every term must match the
/// address (city, region, district, address line) or the English or Arabic name of the area, city, country or compound.
/// </param>
/// <param name="Sort">Null means <see cref="PropertyListSortOrder.Relevance"/>.</param>
/// <param name="OwnerUserId">Only this owner's properties (set for a non-administrator reader).</param>
/// <param name="CityId">Restrict to properties whose area is in this city.</param>
/// <param name="CompoundId">Restrict to one compound.</param>
/// <param name="MinBathrooms">Inclusive lower bound on bathrooms.</param>
/// <param name="MaxBathrooms">Inclusive upper bound on bathrooms.</param>
/// <param name="Furnished">Only furnished (true) or unfurnished (false) properties.</param>
/// <param name="FinishingStatuses">Restrict to any of these finishing statuses. Null or empty means any.</param>
/// <param name="MinAreaSqm">Inclusive lower bound on the area in square metres.</param>
/// <param name="MaxAreaSqm">Inclusive upper bound on the area in square metres.</param>
public sealed record GetPropertiesQuery(
    IReadOnlyList<PropertyType>? Types,
    PropertyStatus? Status,
    string? CountryCode,
    int? Page,
    int? PageSize,
    string? District = null,
    Guid? AreaId = null,
    int? MinBedrooms = null,
    int? MaxBedrooms = null,
    decimal? MinMatchPercentage = null,
    Guid? MatchUserId = null,
    string? Search = null,
    PropertyListSortOrder? Sort = null,
    Guid? OwnerUserId = null,
    Guid? CityId = null,
    Guid? CompoundId = null,
    int? MinBathrooms = null,
    int? MaxBathrooms = null,
    bool? Furnished = null,
    IReadOnlyList<FinishingStatus>? FinishingStatuses = null,
    decimal? MinAreaSqm = null,
    decimal? MaxAreaSqm = null) : IQuery<PagedList<PropertyDetails>>;
