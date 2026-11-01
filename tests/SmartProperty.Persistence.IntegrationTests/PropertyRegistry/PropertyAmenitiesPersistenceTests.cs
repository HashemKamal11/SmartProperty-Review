using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

/// <summary>
/// Proves the amenity reference data is seeded by the migrations, that a property's amenities round-trip through
/// real PostgreSQL, and that the database protects the link table.
/// </summary>
public sealed class PropertyAmenitiesPersistenceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateTimeOffset CreatedAt = TestClock.DefaultNow;

    [Theory]
    [InlineData("Balcony", "شرفة")]
    [InlineData("Security", "أمن")]
    public async Task ArabicNamesAreStoredAndReadBackUnchanged(string code, string nameAr)
    {
        await SeedAmenitiesAsync();
        await using var context = Host.CreateVerificationContext();

        var amenity = await context.Amenities.AsNoTracking().SingleAsync(a => a.Code == code);

        Assert.Equal(nameAr, amenity.NameAr);
    }

    [Fact]
    public async Task ADuplicateAmenityCodeIsRejected()
    {
        await SeedAmenitiesAsync();
        await using var context = Host.CreateVerificationContext();
        context.Amenities.Add(new Amenity(Guid.NewGuid(), "Balcony", "شرفة", "Balcony again", 99));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("ux_registry_amenities_code", postgres.ConstraintName);
    }

    [Fact]
    public async Task APropertysAmenitiesRoundTrip()
    {
        await SeedAmenitiesAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), CreatedAt,
            amenities: await LoadAmenitiesAsync("Balcony", "SwimmingPool", "SharedGym"));

        await SaveAsync(property);

        Assert.Equal(["Balcony", "SwimmingPool", "SharedGym"], await ReadCodesAsync(property.Id));
    }

    [Fact]
    public async Task APropertyWithoutAmenitiesHasNoLinkRows()
    {
        await SeedAmenitiesAsync();
        var property = new Property(Guid.NewGuid(), PropertyType.Land, new PropertyAddress("EG"), CreatedAt);

        await SaveAsync(property);

        Assert.Empty(await ReadCodesAsync(property.Id));
    }

    [Fact]
    public async Task UpdatingTheAmenitiesAddsAndRemovesOnlyTheDifference()
    {
        await SeedAmenitiesAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), CreatedAt,
            amenities: await LoadAmenitiesAsync("Balcony", "SwimmingPool"));
        await SaveAsync(property);

        await using (var updating = Host.CreateVerificationContext())
        {
            var tracked = await updating.Properties
                .Include(p => p.Amenities)
                .SingleAsync(p => p.Id == property.Id);
            var gym = await updating.Amenities.SingleAsync(a => a.Code == "SharedGym");
            var balcony = tracked.Amenities.Single(a => a.Code == "Balcony");

            var changed = tracked.UpdateDetails(
                PropertyType.Unit, new PropertyAddress("EG"), CreatedAt.AddHours(1), amenities: [balcony, gym]);
            await updating.SaveChangesAsync();

            Assert.True(changed);
        }

        Assert.Equal(["Balcony", "SharedGym"], await ReadCodesAsync(property.Id));
    }

    [Fact]
    public async Task ClearingTheAmenitiesRemovesEveryLinkRow()
    {
        await SeedAmenitiesAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), CreatedAt,
            amenities: await LoadAmenitiesAsync("Balcony", "SwimmingPool"));
        await SaveAsync(property);

        await using (var updating = Host.CreateVerificationContext())
        {
            var tracked = await updating.Properties.Include(p => p.Amenities).SingleAsync(p => p.Id == property.Id);
            tracked.UpdateDetails(PropertyType.Unit, new PropertyAddress("EG"), CreatedAt.AddHours(1));
            await updating.SaveChangesAsync();
        }

        Assert.Empty(await ReadCodesAsync(property.Id));
    }

    [Fact]
    public async Task TheSameAmenityCannotBeLinkedTwiceToOneProperty()
    {
        await SeedAmenitiesAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), CreatedAt,
            amenities: await LoadAmenitiesAsync("Balcony"));
        await SaveAsync(property);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            """
            INSERT INTO registry.property_amenities (property_id, amenity_id)
            SELECT property_id, amenity_id FROM registry.property_amenities;
            """));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }

    [Fact]
    public async Task ALinkToAnUnknownAmenityOrPropertyIsRejected()
    {
        await SeedAmenitiesAsync();
        var property = new Property(Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), CreatedAt);
        await SaveAsync(property);

        var unknownAmenity = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO registry.property_amenities (property_id, amenity_id) VALUES ('{property.Id}', '{Guid.NewGuid()}');"));
        var unknownProperty = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO registry.property_amenities (property_id, amenity_id) SELECT '{Guid.NewGuid()}', id FROM registry.amenities LIMIT 1;"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, unknownAmenity.SqlState);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, unknownProperty.SqlState);
    }

    [Fact]
    public async Task AnAmenityInUseCannotBeDeleted()
    {
        await SeedAmenitiesAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), CreatedAt,
            amenities: await LoadAmenitiesAsync("Balcony"));
        await SaveAsync(property);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "DELETE FROM registry.amenities WHERE code = 'Balcony';"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task GetDetailsByIdReturnsTheAmenitiesInOneCommandInDisplayOrder()
    {
        await SeedAmenitiesAsync();
        var property = new Property(
            Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), CreatedAt,
            amenities: await LoadAmenitiesAsync("SwimmingPool", "Balcony", "Security"));
        await SaveAsync(property);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();
        Host.Commands.Reset();

        var details = await repository.GetDetailsByIdAsync(property.Id);

        Assert.NotNull(details);
        Assert.Equal(["Balcony", "Security", "SwimmingPool"], details.Amenities!.Select(a => a.Code));
        Assert.Equal("شرفة", details.Amenities![0].NameAr);
        Assert.Equal("Balcony", details.Amenities![0].NameEn);
        Assert.Equal(1, Host.Commands.Count);
    }

    [Fact]
    public async Task ListReturnsEachPropertysOwnAmenitiesInTwoCommands()
    {
        await SeedAmenitiesAsync();
        var withPool = new Property(
            Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), CreatedAt,
            amenities: await LoadAmenitiesAsync("SwimmingPool"));
        var withBoth = new Property(
            Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), CreatedAt.AddMinutes(1),
            amenities: await LoadAmenitiesAsync("Balcony", "SharedGym"));
        var withNone = new Property(
            Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("EG"), CreatedAt.AddMinutes(2));
        await SaveAsync(withPool);
        await SaveAsync(withBoth);
        await SaveAsync(withNone);

        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPropertyRepository>();
        Host.Commands.Reset();

        var page = await repository.ListAsync(null, null, null, 0, 10);

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(2, Host.Commands.Count);
        Assert.Empty(page.Items.Single(p => p.Id == withNone.Id).Amenities!);
        Assert.Equal(["SwimmingPool"], page.Items.Single(p => p.Id == withPool.Id).Amenities!.Select(a => a.Code));
        Assert.Equal(["Balcony", "SharedGym"], page.Items.Single(p => p.Id == withBoth.Id).Amenities!.Select(a => a.Code));
    }

    [Fact]
    public async Task TheAmenityRepositoryResolvesCodesAndListsEverything()
    {
        await SeedAmenitiesAsync();
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAmenityRepository>();

        var found = await repository.GetByCodesAsync(["Balcony", "Nope", "Security"]);
        var all = await repository.ListAsync();

        Assert.Equal(["Balcony", "Security"], found.Select(a => a.Code));
        Assert.Equal(4, all.Count);
        Assert.Equal("Balcony", all[0].Code);
    }

    // The model-built test schema carries no seed data, so each test inserts the amenities it needs.
    private async Task SeedAmenitiesAsync()
    {
        await using var context = Host.CreateVerificationContext();
        context.Amenities.AddRange(
            new Amenity(Guid.NewGuid(), "Balcony", "شرفة", "Balcony", 1),
            new Amenity(Guid.NewGuid(), "Security", "أمن", "Security", 2),
            new Amenity(Guid.NewGuid(), "SwimmingPool", "مسبح", "Swimming pool", 3),
            new Amenity(Guid.NewGuid(), "SharedGym", "نادي رياضي عام", "Shared gym", 4));
        await context.SaveChangesAsync();
    }

    private async Task<Amenity[]> LoadAmenitiesAsync(params string[] codes)
    {
        await using var context = Host.CreateVerificationContext();
        var amenities = await context.Amenities.AsNoTracking().Where(a => codes.Contains(a.Code)).ToArrayAsync();
        Assert.Equal(codes.Length, amenities.Length);

        return amenities;
    }

    // Detached amenities are attached by key, so saving the property links them rather than inserting new rows.
    private async Task SaveAsync(Property property)
    {
        await using var writing = Host.CreateVerificationContext();
        foreach (var amenity in property.Amenities)
        {
            writing.Attach(amenity);
        }

        writing.Properties.Add(property);
        await writing.SaveChangesAsync();
    }

    private async Task<string[]> ReadCodesAsync(Guid propertyId)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT a.code
            FROM registry.property_amenities pa
            JOIN registry.amenities a ON a.id = pa.amenity_id
            WHERE pa.property_id = @id
            ORDER BY a.sort_order;
            """,
            connection);
        command.Parameters.AddWithValue("id", propertyId);

        var codes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            codes.Add(reader.GetString(0));
        }

        return codes.ToArray();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
