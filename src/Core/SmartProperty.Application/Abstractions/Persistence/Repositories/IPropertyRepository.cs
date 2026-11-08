using SmartProperty.Application.PropertyRegistry;
using SmartProperty.Application.PropertyRegistry.List;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IPropertyRepository
{
    Task AddAsync(Property property, CancellationToken cancellationToken = default);

    /// <summary>
    /// The tracked aggregate, for a command that mutates it and commits through the unit of work, or null when no
    /// property has the id. Not a read model: reads use <see cref="GetDetailsByIdAsync"/>.
    /// </summary>
    Task<Property?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The subset of <paramref name="ids"/> that are properties of the registry, whatever their status.</summary>
    Task<IReadOnlyCollection<Guid>> GetExistingIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

    /// <summary>The read model of one property, whatever its status, or null when no property has the id.</summary>
    Task<PropertyDetails?> GetDetailsByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The read models of the properties among <paramref name="ids"/>, whatever their status, in one query. An id that is
    /// not a property is simply absent from the result.
    /// </summary>
    Task<IReadOnlyList<PropertyDetails>> GetDetailsByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// One page of properties matching every filter in <paramref name="filter"/>, newest first and tie-broken by id,
    /// together with the total the filters match. When the filter names a match user, each item carries that user's
    /// latest stored match percentage.
    /// </summary>
    Task<PropertyPage> SearchAsync(
        PropertyListFilter filter,
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}
