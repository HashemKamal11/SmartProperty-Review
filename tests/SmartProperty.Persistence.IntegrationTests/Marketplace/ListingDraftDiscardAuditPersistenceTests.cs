using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

// Separately gated database tests. Never include this class in offline validation filters.
public sealed class ListingDraftDiscardAuditPersistenceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string SubmissionRevisionMigration = "20261027162411_AddListingSubmissionRevision";
    private const string AuditMigration = "20261028072837_AddListingDraftDiscardAudit";
    private const string NotificationsMigration = "20261115124417_AddNotifications";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        // Only the per-test database owned by DatabaseTest, never its shared template.
        await using var context = Host.CreateVerificationContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.MigrateAsync();
    }

    private async Task InsertAsync(NpgsqlConnection connection)
    {
        // Schema fixture only: this does not represent an authorized application discard.
        await using var command = new NpgsqlCommand("""
            INSERT INTO marketplace.draft_discard_audits
            (event_id, occurred_at, actor_user_id, submission_id, listing_id, property_id, purpose, version, revision, listing_deleted)
            VALUES (@event, @at, @actor, @submission, @listing, @property, 'Sale', 1, 1, false)
            """, connection);
        foreach (var name in new[] { "event", "actor", "submission", "listing", "property" })
            command.Parameters.AddWithValue(name, Guid.NewGuid());
        command.Parameters.AddWithValue("at", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData("UPDATE marketplace.draft_discard_audits SET revision = 2")]
    [InlineData("DELETE FROM marketplace.draft_discard_audits")]
    [InlineData("TRUNCATE marketplace.draft_discard_audits")]
    public async Task DatabaseRejectsAuditMutation(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await InsertAsync(connection);
        await using var command = new NpgsqlCommand(sql, connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_marketplace_discard_audits_append_only", error.ConstraintName);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM marketplace.draft_discard_audits", connection);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DowngradeCannotEraseExistingAuditHistory()
    {
        // Recreate this test's own database, migrated only up to the audit migration, so the downgrade reaches the
        // original audit guard; AddNotifications, which refuses every automatic rollback, is never applied here.
        await using (var deleting = Host.CreateVerificationContext())
        {
            await deleting.Database.EnsureDeletedAsync();
        }

        await using (var migrating = Host.CreateVerificationContext())
        {
            await migrating.GetService<IMigrator>().MigrateAsync(AuditMigration);
        }

        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await InsertAsync(connection);
        await using (var context = Host.CreateVerificationContext())
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => context.GetService<IMigrator>()
                .MigrateAsync(SubmissionRevisionMigration));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("Cannot roll back a nonempty discard audit table.", error.MessageText);
        }

        await using var count = new NpgsqlCommand("SELECT count(*) FROM marketplace.draft_discard_audits", connection);
        Assert.Equal(1L, await count.ExecuteScalarAsync());

        await using var verifying = Host.CreateVerificationContext();
        var applied = (await verifying.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.Equal(AuditMigration, applied[^1]);
        Assert.DoesNotContain(NotificationsMigration, applied);
        await using var notificationsTable = new NpgsqlCommand(
            "SELECT to_regclass('notifications.notifications') IS NULL", connection);
        Assert.Equal(true, await notificationsTable.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AuditInsertRollsBackWithItsTransaction()
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertAsync(connection);
        await transaction.RollbackAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM marketplace.draft_discard_audits", connection);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }
}
