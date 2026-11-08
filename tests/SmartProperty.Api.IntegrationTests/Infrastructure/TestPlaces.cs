using SmartProperty.Domain.Locations;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Stores a country, a city and an area for a test that creates or updates a property: the property's address now comes
/// from the area it is placed in, so every such request names one.
/// </summary>
internal static class TestPlaces
{
    // The AI service ids are unique per level and the database is shared by the tests, so each place takes a new one.
    private static int nextAiId = 1_000_000 + Random.Shared.Next(0, 100_000) * 10;

    /// <summary>Stores a Saudi Arabia / Riyadh / Al Malqa place and returns it. Pass another country name to change the country.</summary>
    public static Place Seed(
        ApiPostgreSqlFixture fixture,
        string countryNameEn = "Saudi Arabia",
        string cityNameEn = "Riyadh",
        string areaNameEn = "Al Malqa")
    {
        var country = new Country(Guid.NewGuid(), Interlocked.Increment(ref nextAiId), countryNameEn, countryNameEn);
        var city = new City(Guid.NewGuid(), Interlocked.Increment(ref nextAiId), country.Id, cityNameEn, cityNameEn);
        var area = new Area(Guid.NewGuid(), Interlocked.Increment(ref nextAiId), city.Id, areaNameEn, areaNameEn);
        var compound = new Compound(Guid.NewGuid(), Interlocked.Increment(ref nextAiId), area.Id, "Garden Residence", "Garden Residence");

        using var context = fixture.CreateContext();
        context.Countries.Add(country);
        context.Cities.Add(city);
        context.Areas.Add(area);
        context.Compounds.Add(compound);
        context.SaveChanges();

        return new Place(country.Code, countryNameEn, cityNameEn, areaNameEn, area.Id, compound.Id);
    }

    /// <summary>The ids and the names a test can expect back on the property's address.</summary>
    public sealed record Place(
        string? CountryCode,
        string CountryNameEn,
        string CityNameEn,
        string AreaNameEn,
        Guid AreaId,
        Guid CompoundId);
}
