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
    int? PageSize) : IQuery<PagedList<PublicListingSummary>>;
