using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>Proves the property images migration creates the table with its rules, keeps existing data, and rolls back.</summary>
public sealed class PropertyImagesMigrationTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string Previous = "20261103090000_AddFavoriteProperties";
    private const string Exists = "SELECT to_regclass('registry.property_images') IS NOT NULL;";

    [Fact]
    public async Task TheMigrationCreatesTheTableWithItsKeysChecksAndIndexes()
    {
        await MigrateToAsync(Previous);
        Assert.Equal(false, await ScalarAsync(Exists));

        await MigrateToLatestAsync();

        Assert.Equal(true, await ScalarAsync(Exists));
        Assert.Equal(
            [
                "FK_property_images_properties_property_id",
                "PK_property_images",
                "ck_registry_property_images_content_type",
                "ck_registry_property_images_size",
                "ck_registry_property_images_sort_order"
            ],
            (await ColumnAsync(
                "SELECT conname FROM pg_constraint WHERE conrelid = 'registry.property_images'::regclass ORDER BY 1 COLLATE \"C\";"))
                .Select(value => (string)value!));
        Assert.Equal(
            ["IX_property_images_property_id_sort_order", "PK_property_images", "ux_registry_property_images_storage_key"],
            (await ColumnAsync(
                "SELECT indexname FROM pg_indexes WHERE schemaname = 'registry' AND tablename = 'property_images' ORDER BY 1 COLLATE \"C\";"))
                .Select(value => (string)value!));
        Assert.Equal(
            ["content_type", "created_at", "file_name", "id", "property_id", "size_bytes", "sort_order", "storage_key"],
            (await ColumnAsync(
                "SELECT column_name FROM information_schema.columns WHERE table_schema = 'registry' AND table_name = 'property_images' ORDER BY 1 COLLATE \"C\";"))
                .Select(value => (string)value!));
    }

    [Fact]
    public async Task TheMigrationLeavesExistingPropertiesAlone()
    {
        await MigrateToAsync(Previous);
        var propertyId = Guid.NewGuid();
        await ExecuteAsync(
            $"INSERT INTO registry.properties (id, type, status, address_country_code, created_at, updated_at) VALUES ('{propertyId}', 'Villa', 'Active', 'EG', now(), now());");

        await MigrateToLatestAsync();

        Assert.Equal("Villa", await ScalarAsync($"SELECT type FROM registry.properties WHERE id = '{propertyId}';"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM registry.property_images;"));
    }

    [Fact]
    public async Task RollingBackDropsTheTableAndTheMigrationCanBeAppliedAgain()
    {
        await MigrateToLatestFromScratchAsync();

        await MigrateToAsync(Previous);
        Assert.Equal(false, await ScalarAsync(Exists));

        await MigrateToLatestAsync();
        Assert.Equal(true, await ScalarAsync(Exists));
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
            return (bool)(await ScalarAsync("SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL;"))!;
        }
        catch (PostgresException)
        {
            return false;
        }
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        var value = await command.ExecuteScalarAsync();

        return value is DBNull ? null : value;
    }

    private async Task<List<object?>> ColumnAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();

        var values = new List<object?>();
        while (await reader.ReadAsync())
        {
            values.Add(await reader.IsDBNullAsync(0) ? null : reader.GetValue(0));
        }

        return values;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
