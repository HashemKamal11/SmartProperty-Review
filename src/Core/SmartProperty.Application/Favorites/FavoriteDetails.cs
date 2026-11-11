using SmartProperty.Application.PropertyRegistry;

namespace SmartProperty.Application.Favorites;

/// <summary>One favorite: the property, with when the user added it.</summary>
public sealed record FavoriteDetails(
    DateTimeOffset FavoritedAt,
    PropertyDetails Property,
    IReadOnlyList<SmartProperty.Application.Marketplace.PublicListings.PublicListingSummary>? Listings = null);

/// <summary>One page of a user's favorites with the total they have, from one repository call.</summary>
public sealed record FavoritePage(IReadOnlyList<FavoriteDetails> Items, long TotalCount);
