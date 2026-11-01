using SmartProperty.Domain.PropertyMatching;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IPropertyMatchRepository
{
    Task AddAsync(PropertyMatch propertyMatch, CancellationToken cancellationToken = default);
}
