using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Locations;
using SmartProperty.Domain.Locations;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// An in-memory place tree. Places are seeded top down, so each one already holds its parent, the way a tracked
/// area holds its city and country.
/// </summary>
internal sealed class FakeLocationRepository : ILocationRepository
{
    public List<Country> Countries { get; } = [];

    public List<City> Cities { get; } = [];

    public List<Area> Areas { get; } = [];

    public List<Compound> Compounds { get; } = [];

    public List<(LocationLevel Level, Guid? ParentId, IReadOnlyCollection<AiPlace> Places)> AddCalls { get; } = [];

    public int ListCallCount { get; private set; }

    public Country SeedCountry(int aiId = 1, string nameEn = "Egypt", string nameAr = "مصر")
    {
        var country = new Country(Guid.NewGuid(), aiId, nameEn, nameAr);
        Countries.Add(country);

        return country;
    }

    public City SeedCity(Country country, int aiId = 3, string nameEn = "Cairo", string nameAr = "القاهرة")
    {
        var city = new City(Guid.NewGuid(), aiId, country, nameEn, nameAr);
        Cities.Add(city);

        return city;
    }

    public Area SeedArea(City city, int aiId = 30, string nameEn = "Helwan", string nameAr = "حلوان")
    {
        var area = new Area(Guid.NewGuid(), aiId, city, nameEn, nameAr);
        Areas.Add(area);

        return area;
    }

    public Compound SeedCompound(Area area, int aiId = 7, string nameEn = "Garden Residence", string nameAr = "جاردن ريزيدنس")
    {
        var compound = new Compound(Guid.NewGuid(), aiId, area, nameEn, nameAr);
        Compounds.Add(compound);

        return compound;
    }

    public Task<Area?> GetAreaAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Areas.FirstOrDefault(area => area.Id == id));
    }

    public Task<Compound?> GetCompoundAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Compounds.FirstOrDefault(compound => compound.Id == id));
    }

    public Task<IReadOnlyList<LocationDetails>> ListAsync(
        LocationLevel level,
        Guid? parentId,
        CancellationToken cancellationToken = default)
    {
        ListCallCount++;

        IEnumerable<LocationDetails> places = level switch
        {
            LocationLevel.Country => Countries.Select(place => new LocationDetails(place.Id, place.NameEn, place.NameAr)),
            LocationLevel.City => Cities.Where(place => place.CountryId == parentId)
                .Select(place => new LocationDetails(place.Id, place.NameEn, place.NameAr)),
            LocationLevel.Area => Areas.Where(place => place.CityId == parentId)
                .Select(place => new LocationDetails(place.Id, place.NameEn, place.NameAr)),
            LocationLevel.Compound => Compounds.Where(place => place.AreaId == parentId)
                .Select(place => new LocationDetails(place.Id, place.NameEn, place.NameAr)),
            _ => []
        };

        IReadOnlyList<LocationDetails> ordered = places.OrderBy(place => place.NameEn).ToList();

        return Task.FromResult(ordered);
    }

    public Task<int?> GetAiIdAsync(LocationLevel level, Guid id, CancellationToken cancellationToken = default)
    {
        int? aiId = level switch
        {
            LocationLevel.Country => Countries.FirstOrDefault(place => place.Id == id)?.AiId,
            LocationLevel.City => Cities.FirstOrDefault(place => place.Id == id)?.AiId,
            LocationLevel.Area => Areas.FirstOrDefault(place => place.Id == id)?.AiId,
            LocationLevel.Compound => Compounds.FirstOrDefault(place => place.Id == id)?.AiId,
            _ => null
        };

        return Task.FromResult(aiId);
    }

    public Task AddAsync(
        LocationLevel level,
        Guid? parentId,
        IReadOnlyCollection<AiPlace> places,
        CancellationToken cancellationToken = default)
    {
        AddCalls.Add((level, parentId, places));

        foreach (var place in places)
        {
            switch (level)
            {
                case LocationLevel.Country:
                    Countries.Add(new Country(Guid.NewGuid(), place.Id, place.NameEn, place.NameAr));
                    break;
                case LocationLevel.City:
                    Cities.Add(new City(Guid.NewGuid(), place.Id, parentId!.Value, place.NameEn, place.NameAr));
                    break;
                case LocationLevel.Area:
                    Areas.Add(new Area(Guid.NewGuid(), place.Id, parentId!.Value, place.NameEn, place.NameAr));
                    break;
                case LocationLevel.Compound:
                    Compounds.Add(new Compound(Guid.NewGuid(), place.Id, parentId!.Value, place.NameEn, place.NameAr));
                    break;
            }
        }

        return Task.CompletedTask;
    }
}
