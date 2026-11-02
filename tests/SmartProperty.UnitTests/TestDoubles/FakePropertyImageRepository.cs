using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.UnitTests.TestDoubles;

internal sealed class FakePropertyImageRepository : IPropertyImageRepository
{
    public List<PropertyImage> Stored { get; } = [];

    public List<PropertyImage> Added { get; } = [];

    public Task<IReadOnlyList<PropertyImage>> ListAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PropertyImage> images = Stored
            .Where(image => image.PropertyId == propertyId)
            .OrderBy(image => image.SortOrder)
            .ToList();

        return Task.FromResult(images);
    }

    public Task<int> CountAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Stored.Count(image => image.PropertyId == propertyId));
    }

    public Task<int> NextSortOrderAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        var own = Stored.Where(image => image.PropertyId == propertyId).ToList();

        return Task.FromResult(own.Count == 0 ? 0 : own.Max(image => image.SortOrder) + 1);
    }

    public Task AddAsync(PropertyImage image, CancellationToken cancellationToken = default)
    {
        Added.Add(image);
        Stored.Add(image);

        return Task.CompletedTask;
    }

    public Task<PropertyImage?> FindAsync(Guid propertyId, Guid imageId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Stored.FirstOrDefault(image => image.PropertyId == propertyId && image.Id == imageId));
    }

    public Task<string?> RemoveAsync(Guid propertyId, Guid imageId, CancellationToken cancellationToken = default)
    {
        var found = Stored.FirstOrDefault(image => image.PropertyId == propertyId && image.Id == imageId);

        if (found is null)
        {
            return Task.FromResult<string?>(null);
        }

        Stored.Remove(found);

        return Task.FromResult<string?>(found.StorageKey);
    }
}
