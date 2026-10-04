using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.PropertyRegistry;

internal sealed class PropertyRepository(ApplicationDbContext dbContext) : IPropertyRepository
{
    public async Task AddAsync(Property property, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);
        await dbContext.Properties.AddAsync(property, cancellationToken);
    }
}
