using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

// Exercises the persistence boundary through DbContext; no Marketplace repository exists in P1A.
public sealed class MarketplaceRepositoryTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DependentFirstGraphInsertsAndMaterializesInFreshContext(bool sale)
    {
        var graph = MarketplaceGraph.Create(sale);
        await using (var context = Host.CreateVerificationContext())
        {
            graph.Track(context);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            Assert.Equal(2, await context.PropertyListings.CountAsync());
        }
        await using var verify = Host.CreateVerificationContext();
        var listing = await verify.PropertyListings.SingleAsync(x => x.Id == graph.Sale.Id);
        Assert.Equal(graph.Property.Id, listing.PropertyId);
        Assert.Equal(ListingPurpose.Sale, listing.Purpose);
        Assert.Equal(graph.Sale.PublicationStatus, listing.PublicationStatus);
        Assert.Equal(graph.Sale.AvailabilityStatus, listing.AvailabilityStatus);
        Assert.Equal(graph.Sale.PublishedSubmissionId, listing.PublishedSubmissionId);
        Assert.Equal(graph.Sale.UpdatedAt, listing.UpdatedAt);
        var purchase = await verify.PurchaseRequests.SingleAsync();
        var rental = await verify.RentalRequests.SingleAsync();
        var reservation = await verify.PropertyMarketplaceReservations.SingleAsync();
        Assert.Equal(graph.Purchase.Status, purchase.Status);
        Assert.Equal(graph.Rental.Status, rental.Status);
        Assert.Equal(graph.Reservation.WinningRequestId, reservation.WinningRequestId);
        Assert.Equal(graph.Reservation.ReservedAt, reservation.ReservedAt);
        Assert.Equal(ListingPurpose.Sale, verify.Entry(purchase).Property<ListingPurpose?>("ListingPurpose").CurrentValue);
        Assert.Equal(ListingPurpose.Rent, verify.Entry(rental).Property<ListingPurpose?>("ListingPurpose").CurrentValue);
        Assert.Equal(sale ? purchase.Id : (Guid?)null, verify.Entry(reservation).Property<Guid?>("PurchaseWinnerId").CurrentValue);
        Assert.Equal(sale ? (Guid?)null : rental.Id, verify.Entry(reservation).Property<Guid?>("RentalWinnerId").CurrentValue);
        Assert.Equal(0, await verify.SaveChangesAsync());
    }

    [Fact]
    public async Task IndependentInMemoryReservationCannotDuplicatePersistedPropertyReservation()
    {
        var graph = MarketplaceGraph.Create();
        await using (var seed = Host.CreateVerificationContext())
        {
            graph.Track(seed);
            await seed.SaveChangesAsync();
        }
        // A second graph is constructible in Domain; PostgreSQL is the uniqueness authority.
        var listing = new PropertyListing(graph.Sale.Id, graph.Property.Id, ListingPurpose.Sale, MarketplaceGraph.Time);
        MarketplaceGraph.Approve(listing, MarketplaceGraph.Time.AddSeconds(1));
        var winner = new PurchaseRequest(graph.Purchase.Id, listing, graph.User.Id, MarketplaceGraph.Time.AddSeconds(2));
        var duplicate = PropertyMarketplaceReservation.AcceptPurchase(listing, null, winner, [], [], MarketplaceGraph.Time.AddSeconds(3));
        await using var context = Host.CreateVerificationContext();
        context.Add(duplicate);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("pk_marketplace_property_reservations", postgres.ConstraintName);
    }
}
