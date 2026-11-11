using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>
/// Countries that were copied before time zones existed get the single zone their code has; a country with no code,
/// or with several zones, stays without one, so a rental in it is refused instead of dated by a guess.
/// </summary>
public sealed class RentalPricingMigrationTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string Previous = "20261110093003_AddAuthorityGrants";
    private const string RentalPricing = "20261110111138_AddRentalPricingOptionsAndCountryTimeZone";

    [Fact]
    public async Task ExistingCountriesGetTheirSingleTimeZoneByCode()
    {
        await MigrateToAsync(Previous);
        await SeedCountriesAsync();

        await MigrateToAsync(RentalPricing);

        var zones = (await QueryAsync("SELECT name_en, time_zone_id FROM locations.countries ORDER BY name_en;"))
            .ToDictionary(row => (string)row[0]!, row => (string?)row[1]);
        Assert.Equal("Africa/Cairo", zones["Egypt"]);
        Assert.Equal("Asia/Riyadh", zones["Saudi Arabia"]);
        Assert.Equal("Asia/Dubai", zones["Emirates"]);
    }

    [Fact]
    public async Task ACountryWithoutACodeOrWithSeveralZonesKeepsNone()
    {
        await MigrateToAsync(Previous);
        await SeedCountriesAsync();

        await MigrateToAsync(RentalPricing);

        var zones = (await QueryAsync("SELECT name_en, time_zone_id FROM locations.countries ORDER BY name_en;"))
            .ToDictionary(row => (string)row[0]!, row => (string?)row[1]);
        Assert.Null(zones["Uncoded"]);
        Assert.Null(zones["United States"]);
    }

    [Fact]
    public async Task RollingBackDropsTheColumnAndTheRentalShapeConstraint()
    {
        await MigrateToAsync(Previous);
        await MigrateToAsync(RentalPricing);

        await MigrateToAsync(Previous);

        Assert.Equal(0L, (await QueryAsync(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'locations' AND table_name = 'countries' AND column_name = 'time_zone_id';")).Single()[0]);
        Assert.Equal(0L, (await QueryAsync(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'ck_marketplace_submissions_rent_terms';")).Single()[0]);
    }

    [Fact]
    public async Task TheRentalShapeConstraintExistsAfterTheMigration()
    {
        await MigrateToAsync(Previous);

        await MigrateToAsync(RentalPricing);

        Assert.Equal(1L, (await QueryAsync(
            "SELECT count(*) FROM pg_constraint WHERE conname = 'ck_marketplace_submissions_rent_terms';")).Single()[0]);
    }

    private async Task SeedCountriesAsync()
    {
        await ExecuteAsync(
            """
            INSERT INTO locations.countries (id, ai_id, name_en, name_ar, code) VALUES
              (gen_random_uuid(), 910001, 'Egypt', 'مصر', 'EG'),
              (gen_random_uuid(), 910002, 'Saudi Arabia', 'السعودية', 'SA'),
              (gen_random_uuid(), 910003, 'Emirates', 'الإمارات', 'AE'),
              (gen_random_uuid(), 910004, 'United States', 'أمريكا', 'US'),
              (gen_random_uuid(), 910005, 'Uncoded', 'بلا رمز', NULL);
            """);
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
