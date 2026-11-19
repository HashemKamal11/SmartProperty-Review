using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>
/// Proves the national-ID country migration marks every ID stored under the old 10-digit rule as Saudi, leaves
/// profiles without an ID alone, and refuses to roll back once a non-Saudi ID exists.
/// </summary>
public sealed class NationalIdCountriesMigrationTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string Previous = "20261110115157_AddRequestDetailsAndCommitments";
    private const string NationalIdCountriesMigration = "20261111092923_SupportEgyptAndUaeNationalIds";

    [Fact]
    public async Task IdsStoredBeforeTheMigrationBecomeSaudiAndProfilesWithoutOneStayEmpty()
    {
        await MigrateToAsync(Previous);
        var withId = await InsertLegacyProfileAsync("'1234567890'");
        var withoutId = await InsertLegacyProfileAsync("NULL");

        await MigrateToLatestAsync();

        Assert.Equal("SA", await ScalarAsync($"SELECT id_country_code FROM identity.owner_profiles WHERE id = '{withId}';"));
        Assert.Equal("1234567890", await ScalarAsync($"SELECT national_id FROM identity.owner_profiles WHERE id = '{withId}';"));
        Assert.Null(await ScalarAsync($"SELECT id_country_code FROM identity.owner_profiles WHERE id = '{withoutId}';"));
    }

    [Fact]
    public async Task RollingBackWorksWithOnlySaudiIdsAndIsRefusedOnceAnotherCountryIsStored()
    {
        await MigrateToAsync(Previous);
        await InsertLegacyProfileAsync("'1234567890'");
        // Up to the migration under test only: AddNotifications refuses every automatic rollback.
        await MigrateToAsync(NationalIdCountriesMigration);

        await MigrateToAsync(Previous);
        Assert.Equal(
            0L,
            await ScalarAsync(
                "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'identity' AND table_name = 'owner_profiles' AND column_name = 'id_country_code';"));

        // Up to the migration under test only: AddNotifications refuses every automatic rollback.
        await MigrateToAsync(NationalIdCountriesMigration);
        var user = await InsertUserAsync();
        await ExecuteAsync(
            "INSERT INTO identity.owner_profiles (id, user_id, status, created_at, updated_at, id_country_code, national_id) "
            + $"VALUES ('{Guid.NewGuid()}', '{user}', 'Draft', now(), now(), 'EG', '29001010101234');");

        var exception = await Assert.ThrowsAsync<PostgresException>(() => MigrateToAsync(Previous));
        Assert.Contains("non-Saudi", exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(
            1L,
            await ScalarAsync("SELECT count(*) FROM identity.owner_profiles WHERE id_country_code = 'EG';"));
    }

    // Before the migration the country column does not exist, so this insert does not name it.
    private async Task<Guid> InsertLegacyProfileAsync(string nationalId)
    {
        var user = await InsertUserAsync();
        var id = Guid.NewGuid();
        await ExecuteAsync(
            "INSERT INTO identity.owner_profiles (id, user_id, status, created_at, updated_at, national_id) "
            + $"VALUES ('{id}', '{user}', 'Draft', now(), now(), {nationalId});");

        return id;
    }

    private async Task<Guid> InsertUserAsync()
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            "INSERT INTO identity.users (id, email, first_name, last_name, status, created_at, updated_at) "
            + $"VALUES ('{id}', '{id:N}@example.test', 'Lee', 'Gacy', 'Active', now(), now());");

        return id;
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

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
