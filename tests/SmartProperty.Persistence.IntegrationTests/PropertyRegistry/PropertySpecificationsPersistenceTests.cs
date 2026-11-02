using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.PropertyRegistry;

/// <summary>
/// Proves the bedrooms, bathrooms, built date, and finishing status columns round-trip through real PostgreSQL,
/// that the finishing status is stored by name, and that the database itself refuses values the domain would.
/// </summary>
public sealed class PropertySpecificationsPersistenceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly DateTimeOffset CreatedAt = TestClock.DefaultNow;

    [Fact]
    public async Task EverySpecificationRoundTrips()
    {
        var property = new Property(
            Guid.NewGuid(),
            PropertyType.Apartment,
            new PropertyAddress("EG"),
            CreatedAt,
            bedrooms: 3,
            bathrooms: 2,
            builtDate: new DateOnly(2021, 6, 30),
            finishingStatus: FinishingStatus.LuxuryFinished);

        await SaveAsync(property);

        var reloaded = await ReloadAsync(property.Id);
        Assert.Equal(3, reloaded.Bedrooms);
        Assert.Equal(2, reloaded.Bathrooms);
        Assert.Equal(new DateOnly(2021, 6, 30), reloaded.BuiltDate);
        Assert.Equal(FinishingStatus.LuxuryFinished, reloaded.FinishingStatus);
    }

    [Fact]
    public async Task AbsentSpecificationsAreStoredAsNull()
    {
        var property = new Property(Guid.NewGuid(), PropertyType.Land, new PropertyAddress("EG"), CreatedAt);

        await SaveAsync(property);

        var reloaded = await ReloadAsync(property.Id);
        Assert.Null(reloaded.Bedrooms);
        Assert.Null(reloaded.Bathrooms);
        Assert.Null(reloaded.BuiltDate);
        Assert.Null(reloaded.FinishingStatus);
        Assert.Null(await ReadColumnAsync(property.Id, "finishing_status"));
    }

    [Theory]
    [InlineData(FinishingStatus.Finished, "Finished")]
    [InlineData(FinishingStatus.LuxuryFinished, "LuxuryFinished")]
    [InlineData(FinishingStatus.SemiFinished, "SemiFinished")]
    [InlineData(FinishingStatus.CoreAndShell, "CoreAndShell")]
    public async Task EachFinishingStatusIsStoredAsItsName(FinishingStatus status, string expected)
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), CreatedAt, finishingStatus: status);

        await SaveAsync(property);

        Assert.Equal(expected, await ReadColumnAsync(property.Id, "finishing_status"));
        Assert.Equal(status, (await ReloadAsync(property.Id)).FinishingStatus);
    }

    [Fact]
    public async Task UpdatingSpecificationsPersistsTheChange()
    {
        var property = new Property(
            Guid.NewGuid(), PropertyType.Apartment, new PropertyAddress("EG"), CreatedAt, bedrooms: 1);
        await SaveAsync(property);

        await using (var updating = Host.CreateVerificationContext())
        {
            var tracked = await updating.Properties.SingleAsync(row => row.Id == property.Id);
            tracked.UpdateDetails(
                PropertyType.Apartment,
                new PropertyAddress("EG"),
                CreatedAt.AddHours(1),
                bedrooms: 4,
                finishingStatus: FinishingStatus.LuxuryFinished);
            await updating.SaveChangesAsync();
        }

        var reloaded = await ReloadAsync(property.Id);
        Assert.Equal(4, reloaded.Bedrooms);
        Assert.Null(reloaded.Bathrooms);
        Assert.Equal(FinishingStatus.LuxuryFinished, reloaded.FinishingStatus);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task BoundaryRoomCountsAreAcceptedByTheDatabase(int count)
    {
        await InsertAsync(bedrooms: count, bathrooms: count);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(101, null)]
    [InlineData(null, -1)]
    [InlineData(null, 101)]
    public async Task OutOfRangeRoomCountsAreRejectedByTheDatabase(int? bedrooms, int? bathrooms)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => InsertAsync(bedrooms: bedrooms, bathrooms: bathrooms));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Contains(
            exception.ConstraintName,
            new[] { "ck_registry_properties_bedrooms_range", "ck_registry_properties_bathrooms_range" });
    }

    [Theory]
    [InlineData("CoreAndShell")]
    [InlineData("LuxuryFinished")]
    public async Task SupportedFinishingStatusNamesAreAcceptedByTheDatabase(string finishingStatus)
    {
        await InsertAsync(finishingStatus: finishingStatus);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("Furnished")]
    [InlineData("Unfinished")]
    [InlineData("furnished")]
    [InlineData("Shiny")]
    [InlineData("")]
    public async Task UnsupportedFinishingStatusNamesAreRejectedByTheDatabase(string finishingStatus)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => InsertAsync(finishingStatus: finishingStatus));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_registry_properties_finishing_status", exception.ConstraintName);
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

    private async Task InsertAsync(int? bedrooms = null, int? bathrooms = null, string? finishingStatus = null)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO registry.properties
                (id, type, status, address_country_code, bedrooms, bathrooms, finishing_status, created_at, updated_at)
            VALUES
                (@id, 'Apartment', 'Active', 'EG', @bedrooms, @bathrooms, @finishing_status, @created_at, @created_at);
            """,
            connection);

        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.Add(new NpgsqlParameter("bedrooms", NpgsqlDbType.Integer) { Value = (object?)bedrooms ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("bathrooms", NpgsqlDbType.Integer) { Value = (object?)bathrooms ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("finishing_status", NpgsqlDbType.Text) { Value = (object?)finishingStatus ?? DBNull.Value });
        command.Parameters.AddWithValue("created_at", CreatedAt);

        await command.ExecuteNonQueryAsync();
    }
}
