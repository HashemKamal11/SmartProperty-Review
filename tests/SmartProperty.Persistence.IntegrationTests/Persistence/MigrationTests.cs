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
    private const string PropertyStatusConcurrencyMigration = "20261013084401_SynchronizePropertyStatusConcurrency";
    private const string WorkspaceRoleNameMigration = "20261015082753_EnforceUniqueWorkspaceRoleNames";

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
    public async Task BlankDatabase_AppliesEveryMigrationEndingWithTheWorkspaceRoleNameInvariant()
    {
        await using (var deleting = Host.CreateVerificationContext())
        {
            await deleting.Database.EnsureDeletedAsync();
        }

        await using var context = Host.CreateVerificationContext();
        await context.Database.MigrateAsync();

        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();

        Assert.Equal(context.Database.GetMigrations(), applied);
        Assert.Equal(WorkspaceRoleNameMigration, applied[^1]);
        Assert.Contains(PropertyStatusConcurrencyMigration, applied);
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
    public async Task WorkspaceRoleNameMigration_AppliesFromPreviousMigrationAndRollsBack()
    {
        await using (var deleting = Host.CreateVerificationContext())
        {
            await deleting.Database.EnsureDeletedAsync();
        }

        await using (var previous = Host.CreateVerificationContext())
        {
            await previous.GetService<IMigrator>().MigrateAsync(PropertyStatusConcurrencyMigration);
        }

        Assert.False(await WorkspaceRoleIndexExistsAsync());

        await using (var current = Host.CreateVerificationContext())
        {
            await current.Database.MigrateAsync();
        }

        Assert.True(await WorkspaceRoleIndexExistsAsync());

        await using (var rollingBack = Host.CreateVerificationContext())
        {
            await rollingBack.GetService<IMigrator>().MigrateAsync(PropertyStatusConcurrencyMigration);

            Assert.DoesNotContain(
                WorkspaceRoleNameMigration,
                await rollingBack.Database.GetAppliedMigrationsAsync());
        }

        Assert.False(await WorkspaceRoleIndexExistsAsync());
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

    private Task<bool> PlatformRoleIndexExistsAsync() => IdentityIndexExistsAsync("ux_identity_roles_platform_name");

    private async Task<bool> WorkspaceRoleIndexExistsAsync()
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'identity'
              AND indexname = 'ux_identity_roles_workspace_name';
            """,
            connection);

        if (await command.ExecuteScalarAsync() is not string indexDefinition)
        {
            return false;
        }

        Assert.Contains("UNIQUE INDEX", indexDefinition);
        Assert.Contains("(workspace_id, name)", indexDefinition);
        Assert.Contains("WHERE ((scope)::text = 'Workspace'::text)", indexDefinition);

        return true;
    }

    private async Task<bool> IdentityIndexExistsAsync(string indexName)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_indexes
                WHERE schemaname = 'identity'
                  AND indexname = @indexName);
            """,
            connection);
        command.Parameters.AddWithValue("indexName", indexName);

        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
