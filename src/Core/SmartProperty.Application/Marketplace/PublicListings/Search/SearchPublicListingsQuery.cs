using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Marketplace.PublicListings.Search;

/// <summary>
/// A public, anonymous listing search. Every value is optional; the combination rules are enforced by the handler.
/// </summary>
public sealed record SearchPublicListingsQuery(
    ListingPurpose? Purpose,
    PropertyType? PropertyType,
    string? CountryCode,
    string? Region,
    string? City,
    string? District,
    decimal? MinPrice,
    decimal? MaxPrice,
    string? Currency,
    RentPeriod? RentPeriod,
    PublicListingSortOrder? Sort,
    int? Page,
    int? PageSize,
    DateOnly? AvailableFrom = null,
    DateOnly? AvailableTo = null,
    IReadOnlyList<PropertyType>? PropertyTypes = null,
    string? Search = null,
    Guid? AreaId = null,
    Guid? CityId = null,
    Guid? CompoundId = null,
    int? MinBedrooms = null,
    int? MaxBedrooms = null,
    decimal? MinMatchPercentage = null,
    Guid? ViewerUserId = null) : IQuery<PagedList<PublicListingSummary>>;
