using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

// Separately gated PostgreSQL tests. Never included in offline validation filters.
public sealed class ListingSubmissionPersistenceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovedSnapshotsMaterializeFaithfully(bool sale)
    {
        var graph = MarketplaceGraph.Create(sale);
        await using (var seed = Host.CreateVerificationContext()) await graph.SeedPrincipalsAsync(seed);
        await using var verify = Host.CreateVerificationContext();
        var submissions = await verify.ListingSubmissions.Include(x => x.ReviewDecision).OrderBy(x => x.Purpose).ToListAsync();
        Assert.Equal(2, submissions.Count);
        Assert.All(submissions, x => Assert.True(x.ReviewDecision!.Approved));
        Assert.Null(submissions.Single(x => x.Purpose == ListingPurpose.Sale).Content.RentPeriod);
        Assert.Equal(RentPeriod.Monthly, submissions.Single(x => x.Purpose == ListingPurpose.Rent).Content.RentPeriod);
        Assert.All(submissions, x => Assert.Single(x.Evidence));
        Assert.Equal(0, await verify.SaveChangesAsync());
    }

    [Fact]
    public async Task PublishedPointerCannotReferenceOppositePurpose()
    {
        var graph = MarketplaceGraph.Create();
        await using (var seed = Host.CreateVerificationContext()) await graph.SeedPrincipalsAsync(seed);
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE marketplace.listings SET published_submission_id = @submission WHERE id = @listing", connection);
        command.Parameters.AddWithValue("submission", graph.RentSubmission.Id);
        command.Parameters.AddWithValue("listing", graph.Sale.Id);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal("fk_marketplace_listings_published_submission", exception.ConstraintName);
    }
}
