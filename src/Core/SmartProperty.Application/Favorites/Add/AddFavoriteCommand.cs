using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Favorites.Add;

/// <summary>
/// Marks a property as a favorite of the user. Idempotent: a property that is already a favorite is reported as such
/// and nothing changes.
/// </summary>
public sealed record AddFavoriteCommand(Guid UserId, Guid PropertyId) : ICommand<AddFavoriteResult>;

/// <param name="Created">True when this call added the favorite; false when it was already there.</param>
public sealed record AddFavoriteResult(Guid PropertyId, DateTimeOffset FavoritedAt, bool Created);
