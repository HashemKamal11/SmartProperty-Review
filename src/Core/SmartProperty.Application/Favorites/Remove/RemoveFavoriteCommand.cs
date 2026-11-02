using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Favorites.Remove;

/// <summary>
/// Removes a property from the user's favorites. Idempotent: removing something that is not a favorite is a success,
/// because the user asked for it not to be one and it is not.
/// </summary>
public sealed record RemoveFavoriteCommand(Guid UserId, Guid PropertyId) : ICommand<RemoveFavoriteResult>;

/// <param name="Removed">True when this call removed a favorite; false when there was none.</param>
public sealed record RemoveFavoriteResult(bool Removed);
