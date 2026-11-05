using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartProperty.Domain.Locations;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>Proves the country code migration adds the column, fills it for known countries, and rolls back.</summary>
public sealed class CountryCodeMigrationTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string Previous = "20261105132710_AddPropertyComparison";

    [Fact]
    public async Task CountriesAlreadyStoredGetTheirCodeFromTheEnglishNameAndUnknownOnesStayWithoutOne()
    {
        await MigrateToAsync(Previous);
        await InsertCountryWithoutCodeAsync(1, "Saudi Arabia");
        await InsertCountryWithoutCodeAsync(2, "Egypt");
        await InsertCountryWithoutCodeAsync(3, "  UAE  ");
        await InsertCountryWithoutCodeAsync(4, "Atlantis");

        await MigrateToLatestAsync();

        Assert.Equal("SA", await ScalarAsync("SELECT code FROM locations.countries WHERE ai_id = 1;"));
        Assert.Equal("EG", await ScalarAsync("SELECT code FROM locations.countries WHERE ai_id = 2;"));
        Assert.Equal("AE", await ScalarAsync("SELECT code FROM locations.countries WHERE ai_id = 3;"));
        Assert.Null(await ScalarAsync("SELECT code FROM locations.countries WHERE ai_id = 4;"));
    }

    [Fact]
    public async Task TheColumnIsTwoCharactersAndOnlyUpperCaseLettersOrNothingAreAccepted()
    {
        await MigrateToLatestFromScratchAsync();

        Assert.Equal(
            "character varying",
            await ScalarAsync(
                "SELECT data_type FROM information_schema.columns WHERE table_schema = 'locations' AND table_name = 'countries' AND column_name = 'code';"));
        Assert.Equal(
            2,
            await ScalarAsync(
                "SELECT character_maximum_length FROM information_schema.columns WHERE table_schema = 'locations' AND table_name = 'countries' AND column_name = 'code';"));

        await InsertCountryAsync(10, "Fine", "'SA'");
        await InsertCountryAsync(11, "No code", "NULL");

        foreach (var bad in new[] { "'sa'", "'S1'", "'S'" })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertCountryAsync(20, "Bad", bad));
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        }
    }

    [Fact]
    public async Task ACountryTheDomainCreatesCarriesTheCodeItLooksUp()
    {
        await MigrateToLatestFromScratchAsync();
        await using var context = Host.CreateVerificationContext();

        context.Countries.Add(new Country(Guid.NewGuid(), 30, "Saudi Arabia", "السعودية"));
        context.Countries.Add(new Country(Guid.NewGuid(), 31, "Atlantis", "أطلانتس"));
        context.Countries.Add(new Country(Guid.NewGuid(), 32, "Atlantis again", "أطلانتس", "xx"));
        await context.SaveChangesAsync();

        var codes = await context.Countries.AsNoTracking()
            .Where(country => country.AiId >= 30)
            .OrderBy(country => country.AiId)
            .Select(country => country.Code)
            .ToListAsync();
        Assert.Equal(["SA", null, "XX"], codes);
    }

    [Fact]
    public async Task RollingBackDropsTheColumnAndTheMigrationCanBeAppliedAgain()
    {
        await MigrateToLatestFromScratchAsync();

        await MigrateToAsync(Previous);

        Assert.Equal(
            0L,
            await ScalarAsync(
                "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'locations' AND table_name = 'countries' AND column_name = 'code';"));

        await InsertCountryWithoutCodeAsync(40, "Egypt");
        await MigrateToLatestAsync();

        Assert.Equal("EG", await ScalarAsync("SELECT code FROM locations.countries WHERE ai_id = 40;"));
    }

    // Before the migration the column does not exist, so that insert does not name it.
    private Task InsertCountryWithoutCodeAsync(int aiId, string nameEn)
    {
        return ExecuteAsync(
            $"INSERT INTO locations.countries (id, ai_id, name_en, name_ar) VALUES ('{Guid.NewGuid()}', {aiId}, '{nameEn}', '{nameEn}');");
    }

    private Task InsertCountryAsync(int aiId, string nameEn, string code)
    {
        return ExecuteAsync(
            $"INSERT INTO locations.countries (id, ai_id, name_en, name_ar, code) VALUES ('{Guid.NewGuid()}', {aiId}, '{nameEn}', '{nameEn}', {code});");
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
