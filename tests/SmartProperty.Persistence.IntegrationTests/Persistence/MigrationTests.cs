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
    private const string PropertyRegistryMigration = "20261001135125_AddPropertyRegistry";

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

    [Fact]
    public async Task BlankDatabase_AppliesEveryMigrationEndingWithThePropertyRegistry()
    {
        await using (var deleting = Host.CreateVerificationContext())
        {
            await deleting.Database.EnsureDeletedAsync();
        }

        await using var context = Host.CreateVerificationContext();
        await context.Database.MigrateAsync();

        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();

        Assert.Equal(context.Database.GetMigrations(), applied);
        Assert.Equal(PropertyRegistryMigration, applied[^1]);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.True(await PropertiesTableExistsAsync());
    }

    [Fact]
    public async Task PropertyRegistryMigration_AppliesFromPreviousMigrationAndRollsBack()
    {
        await using (var deleting = Host.CreateVerificationContext())
        {
            await deleting.Database.EnsureDeletedAsync();
        }

        await using (var previous = Host.CreateVerificationContext())
        {
            await previous.GetService<IMigrator>().MigrateAsync(PlatformRoleInvariantMigration);
        }

        Assert.False(await PropertiesTableExistsAsync());

        await using (var current = Host.CreateVerificationContext())
        {
            await current.Database.MigrateAsync();
        }

        Assert.True(await PropertiesTableExistsAsync());

        await using (var rollingBack = Host.CreateVerificationContext())
        {
            await rollingBack.GetService<IMigrator>().MigrateAsync(PlatformRoleInvariantMigration);

            Assert.DoesNotContain(
                PropertyRegistryMigration,
                await rollingBack.Database.GetAppliedMigrationsAsync());
        }

        Assert.False(await PropertiesTableExistsAsync());
    }

    [Fact]
    public void TheModelSnapshotMapsPropertyAndMatchesTheCurrentModel()
    {
        using var context = Host.CreateVerificationContext();
        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Model.FindEntityType("SmartProperty.Domain.PropertyRegistry.Property"));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    private async Task<bool> PropertiesTableExistsAsync()
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('registry.properties') IS NOT NULL;",
            connection);

        return (bool)(await command.ExecuteScalarAsync())!;
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
