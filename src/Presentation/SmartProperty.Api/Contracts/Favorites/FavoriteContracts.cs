#nullable enable
using SmartProperty.Api.Contracts.PropertyRegistry;

namespace SmartProperty.Api.Contracts.Favorites;

/// <summary>
/// One favorite: the property exactly as the property endpoints return it, when the user added it, and the
/// advertisements currently visible for it (price, terms and pricing options live on listings, not on the property).
/// A property with no visible advertisement has an empty list.
/// </summary>
public sealed record FavoriteResponse(
    DateTimeOffset FavoritedAt,
    PropertyResponse Property,
    IReadOnlyList<SmartProperty.Api.Contracts.Marketplace.Public.PublicListingSummaryResponse>? Listings = null);

/// <summary>Confirms a property is among the user's favorites, and since when.</summary>
public sealed record FavoriteMarkResponse(Guid PropertyId, DateTimeOffset FavoritedAt);
