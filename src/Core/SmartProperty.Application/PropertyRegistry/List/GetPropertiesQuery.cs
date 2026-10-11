using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.List;

/// <summary>
/// Reads the canonical property registry.
/// </summary>
/// <remarks>
/// Deliberately narrow: three exact-match filters and the standard page parameters. It is not a general query
/// language — no free-text search, no sort expression, no field selection.
/// </remarks>
/// <param name="Type">Restrict to one property type. Null means every type.</param>
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
public sealed record GetPropertiesQuery(
    PropertyType? Type,
    PropertyStatus? Status,
    string? CountryCode,
    int? Page,
    int? PageSize) : IQuery<PagedList<PropertyDetails>>;
