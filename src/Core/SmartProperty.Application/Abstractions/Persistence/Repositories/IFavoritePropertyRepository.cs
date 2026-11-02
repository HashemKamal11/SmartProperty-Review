using SmartProperty.Application.Favorites;
using SmartProperty.Domain.Favorites;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IFavoritePropertyRepository
{
    /// <summary>The user's favorite of this property as stored, or null when it is not one. Not tracked.</summary>
    Task<FavoriteProperty?> FindAsync(Guid userId, Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>Tracks a new favorite. Committed by the caller.</summary>
    Task AddAsync(FavoriteProperty favorite, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tracks the removal of the user's favorite of this property and reports whether there was one to remove.
    /// Committed by the caller.
    /// </summary>
    Task<bool> RemoveAsync(Guid userId, Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the user's favorites, most recently added first, each with the full property read model, together
    /// with the total the user has. Archived properties are included: a favorite is never hidden.
    /// </summary>
    Task<FavoritePage> ListAsync(
        Guid userId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>The ids of every property the user has favorited, most recently added first.</summary>
    Task<IReadOnlyList<Guid>> ListPropertyIdsAsync(Guid userId, CancellationToken cancellationToken = default);
}
