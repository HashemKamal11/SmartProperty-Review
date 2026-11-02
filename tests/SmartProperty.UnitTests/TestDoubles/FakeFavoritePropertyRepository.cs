using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Favorites;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Domain.Favorites;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>An in-memory favorites store that records how it was asked.</summary>
internal sealed class FakeFavoritePropertyRepository : IFavoritePropertyRepository
{
    public List<FavoriteProperty> Stored { get; } = [];

    public List<FavoriteProperty> Added { get; } = [];

    public int RemoveCallCount { get; private set; }

    public (Guid UserId, int Skip, int Take)? LastList { get; private set; }

    /// <summary>The read model each property id is listed with; a missing id lists a minimal one.</summary>
    public Dictionary<Guid, PropertyDetails> Details { get; } = [];

    /// <summary>When set, <see cref="FindAsync"/> answers null once and then finds normally, as when a rival wins a race.</summary>
    public bool HideFavoriteOnFirstFind { get; set; }

    private bool _hid;

    public Task<FavoriteProperty?> FindAsync(Guid userId, Guid propertyId, CancellationToken cancellationToken = default)
    {
        if (HideFavoriteOnFirstFind && !_hid)
        {
            _hid = true;

            return Task.FromResult<FavoriteProperty?>(null);
        }

        return Task.FromResult(Stored.FirstOrDefault(favorite => favorite.UserId == userId && favorite.PropertyId == propertyId));
    }

    public Task AddAsync(FavoriteProperty favorite, CancellationToken cancellationToken = default)
    {
        Added.Add(favorite);
        Stored.Add(favorite);

        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(Guid userId, Guid propertyId, CancellationToken cancellationToken = default)
    {
        RemoveCallCount++;

        var found = Stored.FirstOrDefault(favorite => favorite.UserId == userId && favorite.PropertyId == propertyId);

        return Task.FromResult(found is not null && Stored.Remove(found));
    }

    public Task<FavoritePage> ListAsync(Guid userId, int skip, int take, CancellationToken cancellationToken = default)
    {
        LastList = (userId, skip, take);

        var all = Stored
            .Where(favorite => favorite.UserId == userId)
            .OrderByDescending(favorite => favorite.CreatedAt)
            .ThenByDescending(favorite => favorite.PropertyId)
            .ToList();
        IReadOnlyList<FavoriteDetails> page = all
            .Skip(skip)
            .Take(take)
            .Select(favorite => new FavoriteDetails(favorite.CreatedAt, Details.GetValueOrDefault(favorite.PropertyId)
                ?? throw new InvalidOperationException("Seed the property read model first.")))
            .ToList();

        return Task.FromResult(new FavoritePage(page, all.Count));
    }

    public Task<IReadOnlyList<Guid>> ListPropertyIdsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> ids = Stored
            .Where(favorite => favorite.UserId == userId)
            .OrderByDescending(favorite => favorite.CreatedAt)
            .ThenByDescending(favorite => favorite.PropertyId)
            .Select(favorite => favorite.PropertyId)
            .ToList();

        return Task.FromResult(ids);
    }
}
