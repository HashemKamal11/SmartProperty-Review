using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Application.PropertyRegistry.List;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IPropertyRepository
{
    Task AddAsync(Property property, CancellationToken cancellationToken = default);

    /// <summary>The read model of one property, whatever its status, or null when no property has the id.</summary>
    Task<PropertyDetails?> GetDetailsByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of properties matching every non-null filter, ordered newest first and tie-broken by id, together
    /// with the total the filters match. A null <paramref name="status"/> includes Active and Archived alike.
    /// </summary>
    /// <param name="countryCode">An already-normalized upper-case two-letter code, or null for every country.</param>
    Task<PropertyPage> ListAsync(
        PropertyType? type,
        PropertyStatus? status,
        string? countryCode,
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}
