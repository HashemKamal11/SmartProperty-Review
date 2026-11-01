using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.PropertyRegistry;

internal sealed class AmenityRepository(ApplicationDbContext dbContext) : IAmenityRepository
{
    /// <remarks>Tracked, so the property being created or updated can reference the very same instances.</remarks>
    public async Task<IReadOnlyList<Amenity>> GetByCodesAsync(
        IReadOnlyCollection<string> codes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(codes);

        return await dbContext.Amenities
            .Where(amenity => codes.Contains(amenity.Code))
            .OrderBy(amenity => amenity.SortOrder)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AmenityDetails>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.Amenities
            .AsNoTracking()
            .OrderBy(amenity => amenity.SortOrder)
            .Select(amenity => new AmenityDetails(amenity.Code, amenity.NameAr, amenity.NameEn))
            .ToListAsync(cancellationToken);
    }
}
