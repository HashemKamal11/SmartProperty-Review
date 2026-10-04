using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

/// <summary>
/// Proves the <see cref="Property"/> aggregate survives a write to and a read from real PostgreSQL unchanged, and
/// that its enums reach the database as their names rather than their numeric values.
/// </summary>
/// <remarks>
/// Every reload uses a context that did not write the row, so the values come from PostgreSQL rather than from
/// the change tracker. Raw column values are read with plain Npgsql, bypassing the EF value converters.
/// </remarks>
public sealed class PropertyPersistenceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateTimeOffset CreatedAt = TestClock.DefaultNow;

    [Theory]
    [InlineData(PropertyType.Land, "Land")]
    [InlineData(PropertyType.Building, "Building")]
    [InlineData(PropertyType.Unit, "Unit")]
    public async Task EachPropertyTypeIsStoredAsItsName(PropertyType type, string expected)
    {
        var property = new Property(Guid.NewGuid(), type, new PropertyAddress("SA"), CreatedAt);

        await SaveAsync(property);

        Assert.Equal(expected, await ReadColumnAsync(property.Id, "type"));

        var reloaded = await ReloadAsync(property.Id);
        Assert.Equal(type, reloaded.Type);
    }

    [Fact]
    public async Task ANewPropertyIsStoredAsActive()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Land, new PropertyAddress("SA"), CreatedAt);

        await SaveAsync(property);

        Assert.Equal("Active", await ReadColumnAsync(property.Id, "status"));
        Assert.Equal(PropertyStatus.Active, (await ReloadAsync(property.Id)).Status);
    }

    [Fact]
    public async Task EveryFieldRoundTrips()
    {
        var address = new PropertyAddress(
            "sa",
            city: "Riyadh",
            region: "Riyadh Province",
            district: "Al Olaya",
            addressLine: "King Fahd Road 1234",
            postalCode: "12211",
            latitude: 24.7136m,
            longitude: 46.6753m);
        var property = new Property(Guid.NewGuid(), PropertyType.Building, address, CreatedAt);

        await SaveAsync(property);

        var reloaded = await ReloadAsync(property.Id);

        Assert.Equal(property.Id, reloaded.Id);
        Assert.Equal(PropertyType.Building, reloaded.Type);
        Assert.Equal(PropertyStatus.Active, reloaded.Status);
        Assert.Equal("SA", reloaded.Address.CountryCode);
        Assert.Equal("Riyadh", reloaded.Address.City);
        Assert.Equal("Riyadh Province", reloaded.Address.Region);
        Assert.Equal("Al Olaya", reloaded.Address.District);
        Assert.Equal("King Fahd Road 1234", reloaded.Address.AddressLine);
        Assert.Equal("12211", reloaded.Address.PostalCode);
        Assert.Equal(24.7136m, reloaded.Address.Latitude);
        Assert.Equal(46.6753m, reloaded.Address.Longitude);
        Assert.Equal(address, reloaded.Address);
        Assert.Equal(CreatedAt, reloaded.CreatedAt);
        Assert.Equal(CreatedAt, reloaded.UpdatedAt);
    }

    [Fact]
    public async Task APropertyWithOnlyACountryCodeRoundTripsWithEveryOptionalFieldNull()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Land, new PropertyAddress("AE"), CreatedAt);

        await SaveAsync(property);

        var reloaded = await ReloadAsync(property.Id);

        Assert.NotNull(reloaded.Address);
        Assert.Equal("AE", reloaded.Address.CountryCode);
        Assert.Null(reloaded.Address.City);
        Assert.Null(reloaded.Address.Region);
        Assert.Null(reloaded.Address.District);
        Assert.Null(reloaded.Address.AddressLine);
        Assert.Null(reloaded.Address.PostalCode);
        Assert.Null(reloaded.Address.Latitude);
        Assert.Null(reloaded.Address.Longitude);

        foreach (var column in new[]
                 {
                     "address_city", "address_region", "address_district", "address_line", "address_postal_code",
                     "latitude", "longitude"
                 })
        {
            Assert.Null(await ReadColumnAsync(property.Id, column));
        }
    }

    [Theory]
    [InlineData("-90", null)]
    [InlineData("90", null)]
    [InlineData(null, "-180")]
    [InlineData(null, "180")]
    [InlineData("-90", "-180")]
    [InlineData("90", "180")]
    public async Task BoundaryCoordinatesRoundTripIndependently(string? latitude, string? longitude)
    {
        var expectedLatitude = Parse(latitude);
        var expectedLongitude = Parse(longitude);
        var address = new PropertyAddress("SA", latitude: expectedLatitude, longitude: expectedLongitude);
        var property = new Property(Guid.NewGuid(), PropertyType.Land, address, CreatedAt);

        await SaveAsync(property);

        var reloaded = await ReloadAsync(property.Id);

        Assert.Equal(expectedLatitude, reloaded.Address.Latitude);
        Assert.Equal(expectedLongitude, reloaded.Address.Longitude);
    }

    [Fact]
    public async Task CoordinatesAreNotRoundedByTheColumnType()
    {
        // The columns are unconstrained numeric, so a value at decimal's full precision must come back exactly.
        const decimal Latitude = 24.713612345678901234567890123m;
        const decimal Longitude = -46.67531234567890123456789012m;
        var address = new PropertyAddress("SA", latitude: Latitude, longitude: Longitude);
        var property = new Property(Guid.NewGuid(), PropertyType.Land, address, CreatedAt);

        await SaveAsync(property);

        var reloaded = await ReloadAsync(property.Id);

        Assert.Equal(Latitude, reloaded.Address.Latitude);
        Assert.Equal(Longitude, reloaded.Address.Longitude);
    }

    [Fact]
    public async Task ArchivingAPersistedPropertyIsStoredAndReloaded()
    {
        var archivedAt = CreatedAt.AddDays(3);
        var property = new Property(Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("SA"), CreatedAt);

        await SaveAsync(property);

        await using (var archiving = Host.CreateVerificationContext())
        {
            var loaded = await archiving.Properties.SingleAsync(candidate => candidate.Id == property.Id);
            loaded.Archive(archivedAt);
            await archiving.SaveChangesAsync();
        }

        Assert.Equal("Archived", await ReadColumnAsync(property.Id, "status"));

        var reloaded = await ReloadAsync(property.Id);

        Assert.Equal(PropertyStatus.Archived, reloaded.Status);
        Assert.Equal(archivedAt, reloaded.UpdatedAt);
        Assert.Equal(CreatedAt, reloaded.CreatedAt);
    }

    private static decimal? Parse(string? value)
    {
        return value is null ? null : decimal.Parse(value, CultureInfo.InvariantCulture);
    }

    private async Task SaveAsync(Property property)
    {
        await using var writing = Host.CreateVerificationContext();
        writing.Properties.Add(property);
        await writing.SaveChangesAsync();
    }

    private async Task<Property> ReloadAsync(Guid id)
    {
        await using var reading = Host.CreateVerificationContext();
        return await reading.Properties.AsNoTracking().SingleAsync(property => property.Id == id);
    }

    // The column name is always a literal from this class, never input.
    private async Task<object?> ReadColumnAsync(Guid id, string column)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            $"SELECT {column} FROM registry.properties WHERE id = @id;",
            connection);
        command.Parameters.AddWithValue("id", id);

        var value = await command.ExecuteScalarAsync();

        return value is DBNull ? null : value;
    }
}
