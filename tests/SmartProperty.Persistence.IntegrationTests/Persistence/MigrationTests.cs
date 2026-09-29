using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

public sealed class MigrationTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string PreviousMigration = "20260927140557_SynchronizeWorkspaceAccessRequestStatusConcurrency";
    private const string PlatformRoleInvariantMigration = "20260927143034_EnforceUniquePlatformRoleNames";

    [Fact]
    public async Task PlatformRoleInvariantMigration_AppliesFromPreviousMigration()
    {
        await using (var deleting = Host.CreateVerificationContext())
        {
            await deleting.Database.EnsureDeletedAsync();
        }

        await using (var previous = Host.CreateVerificationContext())
        {
            await previous.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        Assert.False(await PlatformRoleIndexExistsAsync());

        await using (var current = Host.CreateVerificationContext())
        {
            await current.Database.MigrateAsync();

            Assert.Contains(
                PlatformRoleInvariantMigration,
                await current.Database.GetAppliedMigrationsAsync());
        }

        Assert.True(await PlatformRoleIndexExistsAsync());
    }

    private async Task<bool> PlatformRoleIndexExistsAsync()
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_indexes
                WHERE schemaname = 'identity'
                  AND indexname = 'ux_identity_roles_platform_name');
            """,
            connection);

        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
