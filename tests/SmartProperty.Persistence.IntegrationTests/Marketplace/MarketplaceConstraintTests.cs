using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

// Requires separately authorized PostgreSQL fixture/schema creation. Never included in the offline filter.
public sealed class MarketplaceConstraintTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    [Theory]
    [InlineData("purpose", "23514", "ck_marketplace_purchase_requests_state")]
    [InlineData("property", "23503", "fk_marketplace_purchase_requests_listing")]
    [InlineData("winner", "23514", "ck_marketplace_property_reservations_winner")]
    [InlineData("wrong_request", "23503", "fk_marketplace_reservations_purchase_winner")]
    public async Task DirectSqlCannotBypassConsistency(string scenario, string sqlState, string constraint)
    {
        var graph = MarketplaceGraph.Create();
        var targetRequestId = graph.Purchase.Id;
        await using (var context = Host.CreateVerificationContext())
        {
            await graph.SeedPrincipalsAsync(context);
            if (scenario == "property")
            {
                // Isolate the request-to-listing FK from reservation winner FKs.
                var at = MarketplaceGraph.Time;
                var property = new Property(Guid.NewGuid(), PropertyType.Unit, new PropertyAddress("SA"), at);
                var listing = new PropertyListing(Guid.NewGuid(), property.Id, ListingPurpose.Sale, at);
                context.Add(property);
                context.Add(listing);
                await context.SaveChangesAsync();
                var submission = MarketplaceGraph.Pending(listing, at.AddSeconds(1));
                context.Add(submission);
                await context.SaveChangesAsync();
                submission.ApproveAndPublish(Guid.NewGuid(), listing, null, MarketplaceGraph.Reviewer(submission),
                    at.AddSeconds(2), true, MarketplaceGraph.Assessments(submission, at.AddSeconds(2)), true, null);
                var independentRequest = new PurchaseRequest(Guid.NewGuid(), listing, graph.User.Id, at.AddSeconds(3));
                context.Add(independentRequest);
                await context.SaveChangesAsync();
                targetRequestId = independentRequest.Id;
                Assert.False(await context.PropertyMarketplaceReservations.AnyAsync(x => x.WinningRequestId == targetRequestId));
            }
        }
        var sql = scenario switch
        {
            "purpose" => "UPDATE marketplace.purchase_requests SET listing_purpose = 'Rent' WHERE id = @id",
            "property" => "UPDATE marketplace.purchase_requests SET property_id = @other WHERE id = @id",
            "winner" => "UPDATE marketplace.property_reservations SET purchase_winner_id = NULL WHERE property_id = @property",
            _ => "UPDATE marketplace.property_reservations SET purchase_winner_id = @other, winning_request_id = @other WHERE property_id = @property"
        };
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", targetRequestId);
        command.Parameters.AddWithValue("property", graph.Property.Id);
        command.Parameters.AddWithValue("other", Guid.NewGuid());
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(sqlState, exception.SqlState);
        Assert.Equal(constraint, exception.ConstraintName);
    }

    [Fact]
    public async Task CanonicalPropertyCannotBeDeletedWhileReferenced()
    {
        var graph = MarketplaceGraph.Create();
        await using var context = Host.CreateVerificationContext();
        await graph.SeedPrincipalsAsync(context);
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("DELETE FROM registry.properties WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", graph.Property.Id);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    [Fact]
    public async Task ConcurrentSamePurposeListingsHaveOneWinner()
    {
        var graph = MarketplaceGraph.Create();
        await using (var seed = Host.CreateVerificationContext())
        {
            seed.Add(graph.Property);
            await seed.SaveChangesAsync();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Insert()
        {
            await using var context = Host.CreateVerificationContext();
            context.Add(new PropertyListing(Guid.NewGuid(), graph.Property.Id, ListingPurpose.Sale, MarketplaceGraph.Time));
            await gate.Task.WaitAsync(timeout.Token);
            try { await context.SaveChangesAsync(timeout.Token); return true; }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_marketplace_listings_property_purpose" })
            { return false; }
        }
        var first = Insert();
        var second = Insert();
        gate.SetResult();
        var outcomes = await Task.WhenAll(first, second);
        Assert.Single(outcomes, success => success);
        await using var verify = Host.CreateVerificationContext();
        Assert.Equal(1, await verify.PropertyListings.CountAsync(x => x.PropertyId == graph.Property.Id));
    }
}
