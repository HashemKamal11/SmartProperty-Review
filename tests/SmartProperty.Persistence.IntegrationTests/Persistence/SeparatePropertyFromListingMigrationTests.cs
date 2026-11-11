using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>
/// Proves the separation of the physical property from its advertisement loses no data: every commercial value is
/// archived before its column is dropped, rows with nothing commercial are not archived, nothing is listed or
/// published, and rolling back restores every value.
/// </summary>
public sealed class SeparatePropertyFromListingMigrationTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string Previous = "20261108122655_AddDailyWeeklyRentPeriods";
    private const string Separation = "20261110082638_SeparatePropertyFromListing";

    private static readonly string[] CommercialColumns =
    [
        "transaction_type", "price", "listing_status", "title_ar", "title_en", "description_ar", "description_en",
        "ai_sync_status", "ai_synced_at", "ai_sync_error",
    ];

    private static readonly Guid Full = Guid.Parse("a0000000-0000-4000-8000-000000000001");
    private static readonly Guid Bare = Guid.Parse("a0000000-0000-4000-8000-000000000002");
    private static readonly Guid FailedSync = Guid.Parse("a0000000-0000-4000-8000-000000000003");
    private static readonly Guid Rented = Guid.Parse("a0000000-0000-4000-8000-000000000004");

    [Fact]
    public async Task EveryCommercialValueIsArchivedAndTheColumnsAreDropped()
    {
        await MigrateToAsync(Previous);
        await SeedLegacyRowsAsync();

        await MigrateToAsync(Separation);

        var archived = await QueryAsync(
            "SELECT property_id, transaction_type, price, listing_status, title_ar, title_en, description_ar, description_en, ai_sync_status, ai_sync_error FROM registry.legacy_property_commercial ORDER BY property_id;");
        Assert.Equal([Full, FailedSync, Rented], archived.Select(row => (Guid)row[0]!));

        var full = archived[0];
        Assert.Equal("Sale", full[1]);
        Assert.Equal(4500000.50m, full[2]);
        Assert.Equal("Reserved", full[3]);
        Assert.Equal("عنوان", full[4]);
        Assert.Equal("A villa", full[5]);
        Assert.Equal("وصف", full[6]);
        Assert.Equal("Description", full[7]);
        Assert.Equal("Synced", full[8]);

        Assert.Equal("Failed", archived[1][8]);
        Assert.Equal("the service timed out", archived[1][9]);
        Assert.Equal("Rented", archived[2][3]);

        Assert.Empty(await RemainingCommercialColumnsAsync());
    }

    [Fact]
    public async Task APropertyWithNothingCommercialIsNotArchivedAndKeepsItsPhysicalFacts()
    {
        await MigrateToAsync(Previous);
        await SeedLegacyRowsAsync();

        await MigrateToAsync(Separation);

        Assert.Equal(0L, (await QueryAsync(
            $"SELECT count(*) FROM registry.legacy_property_commercial WHERE property_id = '{Bare}';")).Single()[0]);
        var physical = (await QueryAsync(
            $"SELECT area_sqm, floor, furnished FROM registry.properties WHERE id = '{Full}';")).Single();
        Assert.Equal(180.50m, physical[0]);
        Assert.Equal(5, physical[1]);
        Assert.Equal(true, physical[2]);
    }

    [Fact]
    public async Task NothingIsListedOrPublishedByTheMigration()
    {
        await MigrateToAsync(Previous);
        await SeedLegacyRowsAsync();

        await MigrateToAsync(Separation);

        Assert.Equal(0L, (await QueryAsync("SELECT count(*) FROM marketplace.listings;")).Single()[0]);
        Assert.Equal(0L, (await QueryAsync("SELECT count(*) FROM marketplace.submissions;")).Single()[0]);
    }

    [Fact]
    public async Task RollingBackRestoresEveryCommercialValueExactly()
    {
        await MigrateToAsync(Previous);
        await SeedLegacyRowsAsync();
        var before = await FingerprintAsync();

        await MigrateToAsync(Separation);
        await MigrateToAsync(Previous);

        Assert.Equal(before, await FingerprintAsync());
        Assert.Null((await QueryAsync("SELECT to_regclass('registry.legacy_property_commercial');")).Single()[0]);
    }

    [Fact]
    public async Task AFreshDatabaseHasAnEmptyArchive()
    {
        await MigrateToAsync(Previous);

        await MigrateToAsync(Separation);

        Assert.Equal(0L, (await QueryAsync("SELECT count(*) FROM registry.legacy_property_commercial;")).Single()[0]);
    }

    // ---- helpers

    private async Task SeedLegacyRowsAsync()
    {
        await ExecuteAsync(
            $"""
            INSERT INTO registry.properties
              (id, type, status, address_country_code, created_at, updated_at, area_sqm, floor, furnished,
               transaction_type, price, listing_status, title_ar, title_en, description_ar, description_en,
               ai_sync_status, ai_synced_at, ai_sync_error)
            VALUES
             ('{Full}','Villa','Active','EG',now(),now(),180.50,5,true,
              'Sale',4500000.50,'Reserved','عنوان','A villa','وصف','Description','Synced',now(),NULL),
             ('{Bare}','Land','Active','EG',now(),now(),NULL,NULL,NULL,
              NULL,NULL,'Available',NULL,NULL,NULL,NULL,'NotSynced',NULL,NULL),
             ('{FailedSync}','Apartment','Active','EG',now(),now(),NULL,NULL,NULL,
              NULL,NULL,'Available',NULL,NULL,NULL,NULL,'Failed',NULL,'the service timed out'),
             ('{Rented}','Studio','Archived','EG',now(),now(),55,2,false,
              'Rent',9000,'Rented',NULL,'Studio',NULL,NULL,'NotSynced',NULL,NULL);
            """);
    }

    private async Task<object?> FingerprintAsync()
    {
        return (await QueryAsync(
            "SELECT md5(string_agg(concat_ws('|',id,transaction_type,price,listing_status,title_ar,title_en,description_ar,description_en,ai_sync_status,ai_synced_at is not null,ai_sync_error), ';' ORDER BY id)) FROM registry.properties;"))
            .Single()[0];
    }

    private async Task<IReadOnlyList<string>> RemainingCommercialColumnsAsync()
    {
        var columns = string.Join(",", CommercialColumns.Select(column => $"'{column}'"));

        return (await QueryAsync(
                $"SELECT column_name FROM information_schema.columns WHERE table_schema = 'registry' AND table_name = 'properties' AND column_name IN ({columns});"))
            .Select(row => (string)row[0]!)
            .ToList();
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
