using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IPropertyRepository
{
    Task AddAsync(Property property, CancellationToken cancellationToken = default);
}
