using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Ai;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Locations;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

/// <summary>
/// Proves the place tree survives real PostgreSQL: the four tables, their links, the one-row-per-AI-id rule, and the
/// repository that fills and reads them.
/// </summary>
public sealed class LocationPersistenceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    // ---- the tables

    [Fact]
    public async Task APlaceTreeRoundTripsWithEveryParentLoaded()
    {
        var country = new Country(Guid.NewGuid(), 1, "Egypt", "مصر");
        var city = new City(Guid.NewGuid(), 3, country, "Cairo", "القاهرة");
        var area = new Area(Guid.NewGuid(), 30, city, "Helwan", "حلوان");
        var compound = new Compound(Guid.NewGuid(), 7, area, "Garden Residence", "جاردن ريزيدنس");
        await using (var writing = Host.CreateVerificationContext())
        {
            writing.Compounds.Add(compound);
            await writing.SaveChangesAsync();
        }

        await using var reading = Host.CreateVerificationContext();
        var reloaded = await reading.Compounds.AsNoTracking()
            .Include(c => c.Area).ThenInclude(a => a.City).ThenInclude(c => c.Country)
            .SingleAsync(c => c.Id == compound.Id);

        Assert.Equal(7, reloaded.AiId);
        Assert.Equal("جاردن ريزيدنس", reloaded.NameAr);
        Assert.Equal(area.Id, reloaded.AreaId);
        Assert.Equal(30, reloaded.Area.AiId);
        Assert.Equal(city.Id, reloaded.Area.CityId);
        Assert.Equal("القاهرة", reloaded.Area.City.NameAr);
        Assert.Equal(country.Id, reloaded.Area.City.CountryId);
        Assert.Equal("مصر", reloaded.Area.City.Country.NameAr);
    }

    [Theory]
    [InlineData("locations.countries", "ux_locations_countries_ai_id")]
    [InlineData("locations.cities", "ux_locations_cities_ai_id")]
    [InlineData("locations.areas", "ux_locations_areas_ai_id")]
    [InlineData("locations.compounds", "ux_locations_compounds_ai_id")]
    public async Task EachLevelKeepsOneRowPerAiId(string table, string index)
    {
        var chain = await SeedChainAsync(aiId: 5);

        var parentColumn = table switch
        {
            "locations.cities" => "country_id",
            "locations.areas" => "city_id",
            "locations.compounds" => "area_id",
            _ => null
        };
        var parentValue = table switch
        {
            "locations.cities" => chain.Country.Id,
            "locations.areas" => chain.City.Id,
            "locations.compounds" => chain.Area.Id,
            _ => Guid.Empty
        };
        var columns = parentColumn is null ? "id, ai_id, name_en, name_ar" : $"id, ai_id, {parentColumn}, name_en, name_ar";
        var values = parentColumn is null
            ? $"'{Guid.NewGuid()}', 5, 'Again', 'مرة أخرى'"
            : $"'{Guid.NewGuid()}', 5, '{parentValue}', 'Again', 'مرة أخرى'";

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO {table} ({columns}) VALUES ({values});"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
        Assert.Equal(index, exception.ConstraintName);
    }

    [Fact]
    public async Task APlaceCannotBeStoredUnderAParentThatDoesNotExist()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO locations.cities (id, ai_id, country_id, name_en, name_ar) VALUES ('{Guid.NewGuid()}', 9, '{Guid.NewGuid()}', 'Nowhere', 'لا مكان');"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task AParentWithChildrenCannotBeDeleted()
    {
        var chain = await SeedChainAsync(aiId: 6);

        var country = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"DELETE FROM locations.countries WHERE id = '{chain.Country.Id}';"));
        var city = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"DELETE FROM locations.cities WHERE id = '{chain.City.Id}';"));
        var area = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"DELETE FROM locations.areas WHERE id = '{chain.Area.Id}';"));

        Assert.All([country, city, area], exception => Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState));
    }

    [Fact]
    public async Task AnOverlongNameIsRefusedByTheDatabase()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO locations.countries (id, ai_id, name_en, name_ar) VALUES ('{Guid.NewGuid()}', 11, '{new string('x', 201)}', 'س');"));

        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, exception.SqlState);
    }

    // ---- the repository

    [Fact]
    public async Task GetAreaLoadsItsCityAndCountry()
    {
        var chain = await SeedChainAsync(aiId: 20);
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ILocationRepository>();

        var area = await repository.GetAreaAsync(chain.Area.Id);

        Assert.NotNull(area);
        Assert.Equal(20, area.AiId);
        Assert.Equal("Cairo", area.City.NameEn);
        Assert.Equal("Egypt", area.City.Country.NameEn);
        Assert.Null(await repository.GetAreaAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetCompoundFindsItAndAnUnknownIdIsNull()
    {
        var chain = await SeedChainAsync(aiId: 21);
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ILocationRepository>();

        var compound = await repository.GetCompoundAsync(chain.Compound.Id);

        Assert.Equal(chain.Area.Id, compound!.AreaId);
        Assert.Null(await repository.GetCompoundAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetAiIdReadsTheAiIdOfEachLevelAndIsNullForAnUnknownIdOrTheWrongLevel()
    {
        var chain = await SeedChainAsync(aiId: 40);
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ILocationRepository>();

        Assert.Equal(40, await repository.GetAiIdAsync(LocationLevel.Country, chain.Country.Id));
        Assert.Equal(40, await repository.GetAiIdAsync(LocationLevel.City, chain.City.Id));
        Assert.Equal(40, await repository.GetAiIdAsync(LocationLevel.Area, chain.Area.Id));
        Assert.Equal(40, await repository.GetAiIdAsync(LocationLevel.Compound, chain.Compound.Id));
        Assert.Null(await repository.GetAiIdAsync(LocationLevel.City, Guid.NewGuid()));
        Assert.Null(await repository.GetAiIdAsync(LocationLevel.Area, chain.Country.Id));
    }

    [Fact]
    public async Task ListReturnsOnlyTheChildrenOfTheParentInEnglishNameOrder()
    {
        var chain = await SeedChainAsync(aiId: 50);
        await AddAreasAsync(chain.City, ("Zamalek", 51), ("Maadi", 52));
        var otherCity = await AddCityAsync(chain.Country, "Giza", 53);
        await AddAreasAsync(otherCity, ("Dokki", 54));
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ILocationRepository>();

        var inCairo = await repository.ListAsync(LocationLevel.Area, chain.City.Id);
        var inGiza = await repository.ListAsync(LocationLevel.Area, otherCity.Id);
        var cities = await repository.ListAsync(LocationLevel.City, chain.Country.Id);

        Assert.Equal(["Helwan", "Maadi", "Zamalek"], inCairo.Select(place => place.NameEn));
        Assert.Equal(["Dokki"], inGiza.Select(place => place.NameEn));
        Assert.Equal(["Cairo", "Giza"], cities.Select(place => place.NameEn));
        Assert.Empty(await repository.ListAsync(LocationLevel.Compound, Guid.NewGuid()));
    }

    [Fact]
    public async Task ListReturnsEveryCountryWhateverTheParentArgument()
    {
        var chain = await SeedChainAsync(aiId: 60);
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ILocationRepository>();

        var withNone = await repository.ListAsync(LocationLevel.Country, null);
        var withOne = await repository.ListAsync(LocationLevel.Country, Guid.NewGuid());

        Assert.Contains(withNone, place => place.Id == chain.Country.Id);
        Assert.Equal(withNone, withOne);
    }

    [Fact]
    public async Task AddStoresThePlacesUnderTheParentAndSkipsOnesAlreadyStored()
    {
        var chain = await SeedChainAsync(aiId: 70);
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ILocationRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await repository.AddAsync(
            LocationLevel.Area,
            chain.City.Id,
            [new AiPlace(70, "Helwan again", "حلوان"), new AiPlace(71, "Maadi", "المعادي"), new AiPlace(71, "Maadi twice", "المعادي")]);
        await unitOfWork.SaveChangesAsync();

        await using var reading = Host.CreateVerificationContext();
        var areas = await reading.Areas.AsNoTracking().Where(a => a.CityId == chain.City.Id).OrderBy(a => a.AiId).ToListAsync();
        Assert.Equal([70, 71], areas.Select(a => a.AiId));
        Assert.Equal("Helwan", areas[0].NameEn);
        Assert.Equal("Maadi", areas[1].NameEn);
    }

    [Fact]
    public async Task AddWithoutAParentBelowTheCountryLevelIsRefused()
    {
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ILocationRepository>();

        await Assert.ThrowsAsync<ArgumentException>(() => repository.AddAsync(
            LocationLevel.City, null, [new AiPlace(80, "Cairo", "القاهرة")]));
    }

    [Fact]
    public async Task ASaveThatRepeatsAnAiIdIsReportedAsTheLocationConstraint()
    {
        await SeedChainAsync(aiId: 90);
        using var scope = Host.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var context = scope.ServiceProvider.GetRequiredService<SmartProperty.Persistence.Context.ApplicationDbContext>();
        context.Countries.Add(new Country(Guid.NewGuid(), 90, "Egypt twice", "مصر"));

        var exception = await Assert.ThrowsAsync<UniqueConstraintViolationException>(() => unitOfWork.SaveChangesAsync());

        Assert.Equal(PersistenceConstraint.LocationAiId, exception.Constraint);
    }

    // ---- helpers

    private async Task<(Country Country, City City, Area Area, Compound Compound)> SeedChainAsync(int aiId)
    {
        var country = new Country(Guid.NewGuid(), aiId, "Egypt", "مصر");
        var city = new City(Guid.NewGuid(), aiId, country, "Cairo", "القاهرة");
        var area = new Area(Guid.NewGuid(), aiId, city, "Helwan", "حلوان");
        var compound = new Compound(Guid.NewGuid(), aiId, area, "Garden Residence", "جاردن ريزيدنس");

        await using var context = Host.CreateVerificationContext();
        context.Compounds.Add(compound);
        await context.SaveChangesAsync();

        return (country, city, area, compound);
    }

    private async Task<City> AddCityAsync(Country country, string name, int aiId)
    {
        var city = new City(Guid.NewGuid(), aiId, country.Id, name, name);

        await using var context = Host.CreateVerificationContext();
        context.Cities.Add(city);
        await context.SaveChangesAsync();

        return city;
    }

    private async Task AddAreasAsync(City city, params (string Name, int AiId)[] areas)
    {
        await using var context = Host.CreateVerificationContext();
        context.Areas.AddRange(areas.Select(a => new Area(Guid.NewGuid(), a.AiId, city.Id, a.Name, a.Name)));
        await context.SaveChangesAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
