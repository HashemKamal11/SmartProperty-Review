using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Locations;
using SmartProperty.Domain.Locations;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Locations;

internal sealed class LocationRepository(ApplicationDbContext dbContext) : ILocationRepository
{
    /// <remarks>Tracked, with the city and country loaded, so a property can be located and then described.</remarks>
    public Task<Area?> GetAreaAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Areas
            .Include(area => area.City)
            .ThenInclude(city => city.Country)
            .FirstOrDefaultAsync(area => area.Id == id, cancellationToken);
    }

    public Task<Compound?> GetCompoundAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Compounds.FirstOrDefaultAsync(compound => compound.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<LocationDetails>> ListAsync(
        LocationLevel level,
        Guid? parentId,
        CancellationToken cancellationToken = default)
    {
        // Name order, English first: the same order whichever language the client shows.
        return level switch
        {
            LocationLevel.Country => await dbContext.Countries.AsNoTracking()
                .OrderBy(place => place.NameEn).ThenBy(place => place.Id)
                .Select(place => new LocationDetails(place.Id, place.NameEn, place.NameAr))
                .ToListAsync(cancellationToken),
            LocationLevel.City => await dbContext.Cities.AsNoTracking()
                .Where(place => place.CountryId == parentId)
                .OrderBy(place => place.NameEn).ThenBy(place => place.Id)
                .Select(place => new LocationDetails(place.Id, place.NameEn, place.NameAr))
                .ToListAsync(cancellationToken),
            LocationLevel.Area => await dbContext.Areas.AsNoTracking()
                .Where(place => place.CityId == parentId)
                .OrderBy(place => place.NameEn).ThenBy(place => place.Id)
                .Select(place => new LocationDetails(place.Id, place.NameEn, place.NameAr))
                .ToListAsync(cancellationToken),
            LocationLevel.Compound => await dbContext.Compounds.AsNoTracking()
                .Where(place => place.AreaId == parentId)
                .OrderBy(place => place.NameEn).ThenBy(place => place.Id)
                .Select(place => new LocationDetails(place.Id, place.NameEn, place.NameAr))
                .ToListAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Location level is not supported.")
        };
    }

    public async Task<int?> GetAiIdAsync(LocationLevel level, Guid id, CancellationToken cancellationToken = default)
    {
        return level switch
        {
            LocationLevel.Country => await dbContext.Countries.AsNoTracking()
                .Where(place => place.Id == id).Select(place => (int?)place.AiId).SingleOrDefaultAsync(cancellationToken),
            LocationLevel.City => await dbContext.Cities.AsNoTracking()
                .Where(place => place.Id == id).Select(place => (int?)place.AiId).SingleOrDefaultAsync(cancellationToken),
            LocationLevel.Area => await dbContext.Areas.AsNoTracking()
                .Where(place => place.Id == id).Select(place => (int?)place.AiId).SingleOrDefaultAsync(cancellationToken),
            LocationLevel.Compound => await dbContext.Compounds.AsNoTracking()
                .Where(place => place.Id == id).Select(place => (int?)place.AiId).SingleOrDefaultAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Location level is not supported.")
        };
    }

    public async Task AddAsync(
        LocationLevel level,
        Guid? parentId,
        IReadOnlyCollection<AiPlace> places,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(places);

        // The AI service may repeat an id, and a place may already be stored; one row per id is the rule.
        var distinct = places.GroupBy(place => place.Id).Select(group => group.First()).ToList();
        var ids = distinct.Select(place => place.Id).ToList();

        switch (level)
        {
            case LocationLevel.Country:
            {
                var known = await dbContext.Countries.Where(place => ids.Contains(place.AiId))
                    .Select(place => place.AiId).ToListAsync(cancellationToken);
                dbContext.Countries.AddRange(distinct.Where(place => !known.Contains(place.Id))
                    .Select(place => new Country(Guid.NewGuid(), place.Id, place.NameEn, place.NameAr)));
                break;
            }

            case LocationLevel.City:
            {
                var known = await dbContext.Cities.Where(place => ids.Contains(place.AiId))
                    .Select(place => place.AiId).ToListAsync(cancellationToken);
                dbContext.Cities.AddRange(distinct.Where(place => !known.Contains(place.Id))
                    .Select(place => new City(Guid.NewGuid(), place.Id, RequireParent(parentId), place.NameEn, place.NameAr)));
                break;
            }

            case LocationLevel.Area:
            {
                var known = await dbContext.Areas.Where(place => ids.Contains(place.AiId))
                    .Select(place => place.AiId).ToListAsync(cancellationToken);
                dbContext.Areas.AddRange(distinct.Where(place => !known.Contains(place.Id))
                    .Select(place => new Area(Guid.NewGuid(), place.Id, RequireParent(parentId), place.NameEn, place.NameAr)));
                break;
            }

            case LocationLevel.Compound:
            {
                var known = await dbContext.Compounds.Where(place => ids.Contains(place.AiId))
                    .Select(place => place.AiId).ToListAsync(cancellationToken);
                dbContext.Compounds.AddRange(distinct.Where(place => !known.Contains(place.Id))
                    .Select(place => new Compound(Guid.NewGuid(), place.Id, RequireParent(parentId), place.NameEn, place.NameAr)));
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(level), level, "Location level is not supported.");
        }
    }

    private static Guid RequireParent(Guid? parentId)
    {
        return parentId ?? throw new ArgumentException("A parent is required below the country level.", nameof(parentId));
    }
}
