using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Favorites;
using SmartProperty.Domain.Favorites;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.Repositories.PropertyRegistry;

namespace SmartProperty.Persistence.Repositories.Favorites;

internal sealed class FavoritePropertyRepository(ApplicationDbContext dbContext) : IFavoritePropertyRepository
{
    public Task<FavoriteProperty?> FindAsync(
        Guid userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.FavoriteProperties
            .AsNoTracking()
            .FirstOrDefaultAsync(
                favorite => favorite.UserId == userId && favorite.PropertyId == propertyId,
                cancellationToken);
    }

    public async Task AddAsync(FavoriteProperty favorite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(favorite);
        await dbContext.FavoriteProperties.AddAsync(favorite, cancellationToken);
    }

    public async Task<bool> RemoveAsync(
        Guid userId,
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var tracked = await dbContext.FavoriteProperties.FirstOrDefaultAsync(
            favorite => favorite.UserId == userId && favorite.PropertyId == propertyId,
            cancellationToken);

        if (tracked is null)
        {
            return false;
        }

        dbContext.FavoriteProperties.Remove(tracked);

        return true;
    }

    /// <remarks>
    /// Three commands: a count over the user's favorites, one page of favorite rows, and the read models of just the
    /// properties on that page, projected in the database by the same expression the property endpoints use. No entity is
    /// materialized or tracked. The order is the most recently added first, tie-broken by the property id, so paging is
    /// stable even when two favorites share an instant.
    /// </remarks>
    public async Task<FavoritePage> ListAsync(
        Guid userId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skip), skip, "Skip must not be negative.");
        }

        if (take < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "Take must be at least 1.");
        }

        var favorites = dbContext.FavoriteProperties
            .AsNoTracking()
            .Where(favorite => favorite.UserId == userId);

        var totalCount = await favorites.LongCountAsync(cancellationToken);

        var page = await favorites
            .OrderByDescending(favorite => favorite.CreatedAt)
            .ThenByDescending(favorite => favorite.PropertyId)
            .Skip(skip)
            .Take(take)
            .Select(favorite => new { favorite.PropertyId, favorite.CreatedAt })
            .ToListAsync(cancellationToken);

        if (page.Count == 0)
        {
            return new FavoritePage([], totalCount);
        }

        var ids = page.Select(row => row.PropertyId).ToList();
        var details = await dbContext.Properties
            .AsNoTracking()
            .Where(property => ids.Contains(property.Id))
            .Select(PropertyDetailsProjection.Expression)
            .ToListAsync(cancellationToken);
        var byId = details.ToDictionary(item => item.Id);

        // The foreign key guarantees every favorite has its property; the page order is the favorites' order.
        var items = page
            .Where(row => byId.ContainsKey(row.PropertyId))
            .Select(row => new FavoriteDetails(row.CreatedAt, byId[row.PropertyId]))
            .ToList();

        return new FavoritePage(items, totalCount);
    }

    public async Task<IReadOnlyList<Guid>> ListPropertyIdsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.FavoriteProperties
            .AsNoTracking()
            .Where(favorite => favorite.UserId == userId)
            .OrderByDescending(favorite => favorite.CreatedAt)
            .ThenByDescending(favorite => favorite.PropertyId)
            .Select(favorite => favorite.PropertyId)
            .ToListAsync(cancellationToken);
    }

}
