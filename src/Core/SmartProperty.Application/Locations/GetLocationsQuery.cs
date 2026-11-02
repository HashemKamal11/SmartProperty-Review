using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Locations;

/// <summary>
/// One level of the place picker: countries, or the cities, areas, or compounds under a parent.
/// </summary>
/// <param name="Level">country (the default), city, area, or compound, in any case.</param>
/// <param name="ParentId">
/// The country for cities, the city for areas, the area for compounds; none for countries. It is this platform's id
/// of the parent, as returned by the previous level.
/// </param>
public sealed record GetLocationsQuery(string? Level, Guid? ParentId = null)
    : IQuery<IReadOnlyList<LocationDetails>>;
