using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.PropertyImages;

internal sealed class PropertyImageRepository(ApplicationDbContext dbContext) : IPropertyImageRepository
{
    public async Task<IReadOnlyList<PropertyImage>> ListAsync(
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.PropertyImages
            .Where(image => image.PropertyId == propertyId)
            .OrderBy(image => image.SortOrder)
            .ThenBy(image => image.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public Task<int> CountAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        return dbContext.PropertyImages.CountAsync(image => image.PropertyId == propertyId, cancellationToken);
    }

    public async Task<int> NextSortOrderAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        var highest = await dbContext.PropertyImages
            .Where(image => image.PropertyId == propertyId)
            .Select(image => (int?)image.SortOrder)
            .MaxAsync(cancellationToken);

        return highest is { } value ? value + 1 : 0;
    }

    public async Task AddAsync(PropertyImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        await dbContext.PropertyImages.AddAsync(image, cancellationToken);
    }

    public Task<PropertyImage?> FindAsync(
        Guid propertyId,
        Guid imageId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.PropertyImages
            .AsNoTracking()
            .FirstOrDefaultAsync(image => image.PropertyId == propertyId && image.Id == imageId, cancellationToken);
    }

    public async Task<string?> RemoveAsync(
        Guid propertyId,
        Guid imageId,
        CancellationToken cancellationToken = default)
    {
        var tracked = await dbContext.PropertyImages.FirstOrDefaultAsync(
            image => image.PropertyId == propertyId && image.Id == imageId,
            cancellationToken);

        if (tracked is null)
        {
            return null;
        }

        dbContext.PropertyImages.Remove(tracked);

        return tracked.StorageKey;
    }
}
