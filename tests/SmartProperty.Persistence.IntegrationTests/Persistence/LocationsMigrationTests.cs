using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>
/// Proves the locations migration builds the place tables and links a property to them, that it can be rolled back
/// to the bare AI ids, and that it leaves the rest of a property alone.
/// </summary>
public sealed class LocationsMigrationTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string Previous = "20261102120000_AddAiSearchIntegration";

    private static readonly DateTimeOffset At = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheMigrationCreatesTheFourTablesAndLinksProperties()
    {
        await MigrateToAsync(Previous);

        await MigrateToLatestAsync();

        Assert.Equal(
            ["areas", "cities", "compounds", "countries"],
            (await QueryAsync("SELECT table_name FROM information_schema.tables WHERE table_schema = 'locations' ORDER BY 1 COLLATE \"C\";"))
                .Select(row => (string)row[0]!));
        Assert.Equal(
            ["area_id", "compound_id"],
            (await QueryAsync(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'registry' AND table_name = 'properties' AND column_name IN ('area_id', 'compound_id') ORDER BY 1 COLLATE \"C\";"))
                .Select(row => (string)row[0]!));
        Assert.Equal(
            ["FK_properties_areas_area_id", "FK_properties_compounds_compound_id"],
            (await QueryAsync(
                "SELECT conname FROM pg_constraint WHERE conrelid = 'registry.properties'::regclass AND contype = 'f' ORDER BY 1 COLLATE \"C\";"))
                .Select(row => (string)row[0]!));
    }

    [Fact]
    public async Task TheBareAiIdColumnsAreReplacedByTheLinksAndTheirCheck()
    {
        await MigrateToAsync(Previous);
        Assert.Equal(
            [2L],
            (await QueryAsync("SELECT count(*) FROM information_schema.columns WHERE table_schema = 'registry' AND table_name = 'properties' AND column_name IN ('ai_area_id', 'ai_compound_id');"))
                .Single().ToArray().Cast<object>().ToArray());

        await MigrateToLatestAsync();

        var row = (await QueryAsync(
            """
            SELECT
                (SELECT count(*) FROM information_schema.columns WHERE table_schema = 'registry' AND table_name = 'properties' AND column_name IN ('ai_area_id', 'ai_compound_id')),
                (SELECT count(*) FROM pg_constraint WHERE conname = 'ck_registry_properties_ai_place'),
                (SELECT count(*) FROM pg_constraint WHERE conname = 'ck_registry_properties_place');
            """)).Single();
        Assert.Equal([0L, 0L, 1L], row.ToArray().Cast<object>().ToArray());
    }

    [Fact]
    public async Task ExistingPropertiesKeepEverythingElseAndHaveNoPlaceYet()
    {
        await MigrateToAsync(Previous);
        var id = Guid.NewGuid();
        await ExecuteAsync(
            $"""
            INSERT INTO registry.properties
                (id, type, status, address_country_code, bedrooms, price, transaction_type, ai_area_id, created_at, updated_at)
            VALUES ('{id}', 'Villa', 'Active', 'EG', 4, 3500000, 'Sale', 12, '{At:O}', '{At:O}');
            """);

        await MigrateToLatestAsync();

        var row = (await QueryAsync(
            $"SELECT type, bedrooms, price, transaction_type, area_id, compound_id FROM registry.properties WHERE id = '{id}';")).Single();
        Assert.Equal("Villa", row[0]);
        Assert.Equal(4, row[1]);
        Assert.Equal(3500000m, row[2]);
        Assert.Equal("Sale", row[3]);
        Assert.Null(row[4]);
        Assert.Null(row[5]);
    }

    [Fact]
    public async Task ACompoundWithoutAnAreaIsRefusedAfterTheMigration()
    {
        await MigrateToLatestFromScratchAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"INSERT INTO registry.properties (id, type, status, address_country_code, compound_id, created_at, updated_at) VALUES ('{Guid.NewGuid()}', 'Villa', 'Active', 'EG', '{Guid.NewGuid()}', now(), now());"));

        Assert.Equal("ck_registry_properties_place", exception.ConstraintName);
    }

    [Fact]
    public async Task TheAiSearchSeedDataIsUntouched()
    {
        await MigrateToLatestFromScratchAsync();

        Assert.Equal([8L], (await QueryAsync("SELECT count(*) FROM registry.views;")).Single().ToArray().Cast<object>().ToArray());
        Assert.Equal([41L], (await QueryAsync("SELECT count(*) FROM registry.amenities;")).Single().ToArray().Cast<object>().ToArray());
    }

    [Fact]
    public async Task RollingBackDropsThePlaceTablesAndRestoresTheBareAiIds()
    {
        await MigrateToLatestFromScratchAsync();

        await MigrateToAsync(Previous);

        var row = (await QueryAsync(
            """
            SELECT
                (SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'locations' AND EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'locations')),
                (SELECT count(*) FROM information_schema.columns WHERE table_schema = 'registry' AND table_name = 'properties' AND column_name IN ('ai_area_id', 'ai_compound_id')),
                (SELECT count(*) FROM information_schema.columns WHERE table_schema = 'registry' AND table_name = 'properties' AND column_name IN ('area_id', 'compound_id')),
                (SELECT count(*) FROM pg_constraint WHERE conname = 'ck_registry_properties_ai_place');
            """)).Single();
        Assert.Equal([0L, 2L, 0L, 1L], row.ToArray().Cast<object>().ToArray());
    }

    [Fact]
    public async Task TheMigrationCanBeAppliedAgainAfterARollback()
    {
        await MigrateToLatestFromScratchAsync();
        await MigrateToAsync(Previous);

        await MigrateToLatestAsync();

        Assert.Equal(
            [4L],
            (await QueryAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema = 'locations';"))
                .Single().ToArray().Cast<object>().ToArray());
    }

    private async Task MigrateToAsync(string target)
    {
        await using (var deleting = Host.CreateVerificationContext())
        {
            // Only the first call of a test starts from nothing; later calls move the existing database.
            if (!await DatabaseHasMigrationHistoryAsync())
            {
                await deleting.Database.EnsureDeletedAsync();
            }
        }

        await using var context = Host.CreateVerificationContext();
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task MigrateToLatestAsync()
    {
        await using var context = Host.CreateVerificationContext();
        await context.Database.MigrateAsync();
    }

    private async Task MigrateToLatestFromScratchAsync()
    {
        await using (var deleting = Host.CreateVerificationContext())
        {
            await deleting.Database.EnsureDeletedAsync();
        }

        await MigrateToLatestAsync();
    }

    private async Task<bool> DatabaseHasMigrationHistoryAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(Host.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL;", connection);

            return (bool)(await command.ExecuteScalarAsync())!;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<List<object?>>> QueryAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<List<object?>>();
        while (await reader.ReadAsync())
        {
            var row = new List<object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row.Add(await reader.IsDBNullAsync(i) ? null : reader.GetValue(i));
            }

            rows.Add(row);
        }

        return rows;
    }
}
