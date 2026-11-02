using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Locations;
using SmartProperty.Domain.Locations;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

/// <summary>
/// The place tree this platform keeps: countries, cities, areas, and compounds, each copied from the AI search
/// service the first time it is needed.
/// </summary>
public interface ILocationRepository
{
    /// <summary>
    /// The tracked area with its city and country loaded, for locating a property, or null when no area has the id.
    /// </summary>
    Task<Area?> GetAreaAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The tracked compound, or null when no compound has the id.</summary>
    Task<Compound?> GetCompoundAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The places of one level in name order. For every level but the first, only those under
    /// <paramref name="parentId"/>; for countries <paramref name="parentId"/> is ignored.
    /// </summary>
    Task<IReadOnlyList<LocationDetails>> ListAsync(
        LocationLevel level,
        Guid? parentId,
        CancellationToken cancellationToken = default);

    /// <summary>The AI service's id of one place, or null when no place of that level has the id.</summary>
    Task<int?> GetAiIdAsync(LocationLevel level, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tracks the places as new rows of <paramref name="level"/> under <paramref name="parentId"/> (ignored for
    /// countries), skipping any whose AI id is already stored. Committed by the caller.
    /// </summary>
    Task AddAsync(
        LocationLevel level,
        Guid? parentId,
        IReadOnlyCollection<AiPlace> places,
        CancellationToken cancellationToken = default);
}
