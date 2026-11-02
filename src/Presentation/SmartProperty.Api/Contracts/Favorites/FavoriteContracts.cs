#nullable enable
using SmartProperty.Api.Contracts.PropertyRegistry;

namespace SmartProperty.Api.Contracts.Favorites;

/// <summary>One favorite: the property exactly as the property endpoints return it, and when the user added it.</summary>
public sealed record FavoriteResponse(DateTimeOffset FavoritedAt, PropertyResponse Property);

/// <summary>Confirms a property is among the user's favorites, and since when.</summary>
public sealed record FavoriteMarkResponse(Guid PropertyId, DateTimeOffset FavoritedAt);
