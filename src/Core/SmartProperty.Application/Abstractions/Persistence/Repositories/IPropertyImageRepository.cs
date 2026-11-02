using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IPropertyImageRepository
{
    /// <summary>The property's images in order, tracked, for changing their order. Empty when it has none.</summary>
    Task<IReadOnlyList<PropertyImage>> ListAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>How many images the property has.</summary>
    Task<int> CountAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>The sort order after the property's last image: 0 when it has none.</summary>
    Task<int> NextSortOrderAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>Tracks a new image. Committed by the caller.</summary>
    Task AddAsync(PropertyImage image, CancellationToken cancellationToken = default);

    /// <summary>One image of the property, not tracked, or null when the property has no such image.</summary>
    Task<PropertyImage?> FindAsync(Guid propertyId, Guid imageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tracks the removal of the image and returns the storage key of the file to delete, or null when the property
    /// has no such image. Committed by the caller.
    /// </summary>
    Task<string?> RemoveAsync(Guid propertyId, Guid imageId, CancellationToken cancellationToken = default);
}
