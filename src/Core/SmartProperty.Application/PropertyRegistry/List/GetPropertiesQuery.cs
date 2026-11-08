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
/// Restrict to one status. Null means every status — Active and Archived alike — because Archived properties
/// remain part of the canonical registry.
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
/// <param name="District">Restrict to one district name (case-insensitive). Null or whitespace means any.</param>
/// <param name="AreaId">Restrict to one registry area. Null means any.</param>
/// <param name="MinPrice">Inclusive lower bound on the listing price. Must not be negative.</param>
/// <param name="MaxPrice">Inclusive upper bound on the listing price. Must not be below <paramref name="MinPrice"/>.</param>
/// <param name="MinBedrooms">Inclusive lower bound on bedrooms (use it alone for "5+").</param>
/// <param name="MaxBedrooms">Inclusive upper bound on bedrooms (equal to <paramref name="MinBedrooms"/> for an exact count).</param>
/// <param name="MinMatchPercentage">
/// 0–100. Keeps only properties whose latest stored match for the signed-in user is at least this value. Requires
/// <paramref name="MatchUserId"/>.
/// </param>
/// <param name="MatchUserId">The signed-in user, or null for an anonymous caller.</param>
public sealed record GetPropertiesQuery(
    IReadOnlyList<PropertyType>? Types,
    PropertyStatus? Status,
    string? CountryCode,
    int? Page,
    int? PageSize,
    string? District = null,
    Guid? AreaId = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    int? MinBedrooms = null,
    int? MaxBedrooms = null,
    decimal? MinMatchPercentage = null,
    Guid? MatchUserId = null) : IQuery<PagedList<PropertyDetails>>;
