using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>
/// The migration that adds dated request terms and commitments: requests that predate it keep their rows and gain no
/// invented dates or prices, the exclusion constraints exist and are deferred, and rolling back removes only what it
/// added.
/// </summary>
public sealed class RequestsAndCommitmentsMigrationTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string Previous = "20261110111934_AddPropertyTimeZone";
    private const string Target = "20261110115157_AddRequestDetailsAndCommitments";

    private static readonly Guid User = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Property = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid RentListing = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    private static readonly Guid SaleListing = Guid.Parse("a0000000-0000-0000-0000-000000000004");
    private static readonly Guid RentalRequest = Guid.Parse("a0000000-0000-0000-0000-000000000005");
    private static readonly Guid PurchaseRequest = Guid.Parse("a0000000-0000-0000-0000-000000000006");

    [Fact]
    public async Task ARequestFromBeforeTheMigrationKeepsItsRowAndGainsNoInventedTermsOrDates()
    {
        await MigrateToAsync(Previous);
        await SeedLegacyRequestsAsync();

        await MigrateToAsync(Target);

        var rental = (await QueryAsync(
            $"SELECT status, submission_id, pricing_period, amount, start_date, end_date, time_zone_id FROM marketplace.rental_requests WHERE id = '{RentalRequest}';")).Single();
        var purchase = (await QueryAsync(
            $"SELECT status, submission_id, offered_amount, currency FROM marketplace.purchase_requests WHERE id = '{PurchaseRequest}';")).Single();
        Assert.Equal("Submitted", rental[0]);
        Assert.All(rental.Skip(1), value => Assert.Null(value));
        Assert.Equal("Submitted", purchase[0]);
        Assert.All(purchase.Skip(1), value => Assert.Null(value));
        Assert.Equal(0L, (await QueryAsync("SELECT count(*) FROM marketplace.property_commitments;")).Single()[0]);
    }

    [Fact]
    public async Task TheTwoExclusionConstraintsExistAndAreDeferredToCommit()
    {
        await MigrateToAsync(Previous);

        await MigrateToAsync(Target);

        var constraints = (await QueryAsync(
            "SELECT conname, condeferrable, condeferred FROM pg_constraint WHERE conname IN ('ex_marketplace_commitments_rental_overlap', 'ex_marketplace_commitments_single_sale') ORDER BY conname;"));
        Assert.Equal(2, constraints.Count);
        Assert.All(constraints, row =>
        {
            Assert.True((bool)row[1]!);
            Assert.True((bool)row[2]!);
        });
        Assert.Equal(1L, (await QueryAsync("SELECT count(*) FROM pg_extension WHERE extname = 'btree_gist';")).Single()[0]);
    }

    [Fact]
    public async Task OnlyOneOpenRequestPerCustomerAndListingIsAllowed()
    {
        await MigrateToAsync(Previous);
        await SeedLegacyRequestsAsync();
        await MigrateToAsync(Target);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            $"""
            INSERT INTO marketplace.rental_requests (id, listing_id, property_id, renter_user_id, status, submitted_at, updated_at, listing_purpose)
            VALUES (gen_random_uuid(), '{RentListing}', '{Property}', '{User}', 'Submitted', now(), now(), 'Rent');
            """));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("ux_marketplace_rental_requests_open", duplicate.ConstraintName);
    }

    [Fact]
    public async Task RollingBackRemovesTheNewObjectsAndKeepsTheRequests()
    {
        await MigrateToAsync(Previous);
        await SeedLegacyRequestsAsync();
        await MigrateToAsync(Target);

        await MigrateToAsync(Previous);

        Assert.Equal(0L, (await QueryAsync(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'marketplace' AND table_name IN ('property_commitments', 'settings');")).Single()[0]);
        Assert.Equal(0L, (await QueryAsync(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = 'marketplace' AND table_name = 'rental_requests' AND column_name IN ('submission_id', 'start_date', 'end_date', 'amount');")).Single()[0]);
        Assert.Equal(1L, (await QueryAsync("SELECT count(*) FROM marketplace.rental_requests;")).Single()[0]);
        Assert.Equal(1L, (await QueryAsync("SELECT count(*) FROM marketplace.purchase_requests;")).Single()[0]);
    }

    private async Task SeedLegacyRequestsAsync()
    {
        await ExecuteAsync(
            $"""
            INSERT INTO identity.users (id, email, first_name, last_name, status, created_at, updated_at)
            VALUES ('{User}', 'legacy@example.test', 'Lee', 'Gacy', 'Active', now(), now());
            INSERT INTO registry.properties (id, type, status, created_at, updated_at, address_country_code)
            VALUES ('{Property}', 'Villa', 'Active', now(), now(), 'EG');
            INSERT INTO marketplace.listings (id, property_id, purpose, publication_status, availability_status, created_at, updated_at, closed_by_reservation)
            VALUES ('{RentListing}', '{Property}', 'Rent', 'Unpublished', 'Open', now(), now(), false),
                   ('{SaleListing}', '{Property}', 'Sale', 'Unpublished', 'Open', now(), now(), false);
            INSERT INTO marketplace.rental_requests (id, listing_id, property_id, renter_user_id, status, submitted_at, updated_at, listing_purpose)
            VALUES ('{RentalRequest}', '{RentListing}', '{Property}', '{User}', 'Submitted', now(), now(), 'Rent');
            INSERT INTO marketplace.purchase_requests (id, listing_id, property_id, buyer_user_id, status, submitted_at, updated_at, listing_purpose)
            VALUES ('{PurchaseRequest}', '{SaleListing}', '{Property}', '{User}', 'Submitted', now(), now(), 'Sale');
            """);
    }

    private async Task MigrateToAsync(string target)
    {
        await using (var deleting = Host.CreateVerificationContext())
        {
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
