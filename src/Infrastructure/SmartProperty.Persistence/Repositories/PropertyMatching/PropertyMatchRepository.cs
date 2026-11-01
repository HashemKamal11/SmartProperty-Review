using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.PropertyMatching;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.PropertyMatching;

internal sealed class PropertyMatchRepository(ApplicationDbContext dbContext) : IPropertyMatchRepository
{
    public async Task AddAsync(PropertyMatch propertyMatch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(propertyMatch);
        await dbContext.PropertyMatches.AddAsync(propertyMatch, cancellationToken);
    }
}
