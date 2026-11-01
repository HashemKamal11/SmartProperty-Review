using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class CrossPurposeReservationTests
{
    [Fact]
    public void PurchaseAcceptance_ReservesSaleClosesRentAndClosesAllSubmittedCompetitors()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var winning = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var competingPurchase = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var competingRental = MarketplaceFixtures.CreateRentalRequest(rent);
        var alreadyRejectedRental = MarketplaceFixtures.CreateRentalRequest(rent);
        alreadyRejectedRental.Reject(MarketplaceFixtures.RequestedAt.AddMinutes(1));

        PropertyMarketplaceReservation.AcceptPurchase(
            sale,
            rent,
            winning,
            new[] { competingPurchase },
            new[] { competingRental, alreadyRejectedRental },
            MarketplaceFixtures.DecidedAt);

        Assert.Equal(PurchaseRequestStatus.Accepted, winning.Status);
        Assert.Equal(ListingPublicationStatus.Withdrawn, sale.PublicationStatus);
        Assert.Equal(ListingAvailabilityStatus.Reserved, sale.AvailabilityStatus);
        Assert.Equal(ListingPublicationStatus.Withdrawn, rent.PublicationStatus);
        Assert.Equal(ListingAvailabilityStatus.Closed, rent.AvailabilityStatus);
        Assert.Equal(PurchaseRequestStatus.ClosedListingUnavailable, competingPurchase.Status);
        Assert.Equal(RentalRequestStatus.ClosedListingUnavailable, competingRental.Status);
        Assert.Equal(RentalRequestStatus.Rejected, alreadyRejectedRental.Status);
    }

    [Fact]
    public void RentalAcceptance_ReservesRentClosesSaleAndClosesAllSubmittedCompetitors()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var winning = MarketplaceFixtures.CreateRentalRequest(rent);
        var competingRental = MarketplaceFixtures.CreateRentalRequest(rent);
        var competingPurchase = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var requesterWithdrawal = MarketplaceFixtures.CreatePurchaseRequest(sale);
        requesterWithdrawal.Withdraw(MarketplaceFixtures.RequestedAt.AddMinutes(1));

        PropertyMarketplaceReservation.AcceptRental(
            rent,
            sale,
            winning,
            new[] { competingRental },
            new[] { competingPurchase, requesterWithdrawal },
            MarketplaceFixtures.DecidedAt);

        Assert.Equal(RentalRequestStatus.Accepted, winning.Status);
        Assert.Equal(ListingPublicationStatus.Withdrawn, rent.PublicationStatus);
        Assert.Equal(ListingAvailabilityStatus.Reserved, rent.AvailabilityStatus);
        Assert.Equal(ListingPublicationStatus.Withdrawn, sale.PublicationStatus);
        Assert.Equal(ListingAvailabilityStatus.Closed, sale.AvailabilityStatus);
        Assert.Equal(RentalRequestStatus.ClosedListingUnavailable, competingRental.Status);
        Assert.Equal(PurchaseRequestStatus.ClosedListingUnavailable, competingPurchase.Status);
        Assert.Equal(PurchaseRequestStatus.WithdrawnByRequester, requesterWithdrawal.Status);
    }

    [Fact]
    public void Acceptance_ClosesAnUnpublishedOppositeIdentityWithoutPublishingIt()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId, publish: false);
        var winning = MarketplaceFixtures.CreatePurchaseRequest(sale);

        PropertyMarketplaceReservation.AcceptPurchase(
            sale,
            rent,
            winning,
            Array.Empty<PurchaseRequest>(),
            Array.Empty<RentalRequest>(),
            MarketplaceFixtures.DecidedAt);

        Assert.Equal(ListingPublicationStatus.Unpublished, rent.PublicationStatus);
        Assert.Equal(ListingAvailabilityStatus.Closed, rent.AvailabilityStatus);
        Assert.Null(rent.PublishedAt);
        Assert.Null(rent.WithdrawnAt);
    }

    [Fact]
    public void SameInMemoryGraph_PreventsSequentialPurchaseAndRentalAcceptance()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var purchase = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var rental = MarketplaceFixtures.CreateRentalRequest(rent);

        PropertyMarketplaceReservation.AcceptPurchase(
            sale,
            rent,
            purchase,
            Array.Empty<PurchaseRequest>(),
            new[] { rental },
            MarketplaceFixtures.DecidedAt);

        Assert.Throws<InvalidOperationException>(() => PropertyMarketplaceReservation.AcceptRental(
            rent,
            sale,
            rental,
            Array.Empty<RentalRequest>(),
            Array.Empty<PurchaseRequest>(),
            MarketplaceFixtures.DecidedAt.AddMinutes(1)));

        Assert.Equal(PurchaseRequestStatus.Accepted, purchase.Status);
        Assert.Equal(RentalRequestStatus.ClosedListingUnavailable, rental.Status);
        Assert.Equal(ListingAvailabilityStatus.Reserved, sale.AvailabilityStatus);
        Assert.Equal(ListingAvailabilityStatus.Closed, rent.AvailabilityStatus);
    }

    [Fact]
    public void ManualWithdrawal_RemainsPurposeLocalAndCreatesNoReservation()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);

        sale.Withdraw(MarketplaceFixtures.DecidedAt);

        Assert.Equal(ListingPublicationStatus.Withdrawn, sale.PublicationStatus);
        Assert.Equal(ListingAvailabilityStatus.Closed, sale.AvailabilityStatus);
        Assert.Equal(ListingPublicationStatus.Published, rent.PublicationStatus);
        Assert.Equal(ListingAvailabilityStatus.Open, rent.AvailabilityStatus);
    }
    [Fact]
    public void IndependentInMemoryGraphs_DoNotEstablishDatabaseWideReservationUniqueness()
    {
        var propertyId = Guid.NewGuid();
        var saleGraph = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var purchase = MarketplaceFixtures.CreatePurchaseRequest(saleGraph);
        var rentGraph = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var rental = MarketplaceFixtures.CreateRentalRequest(rentGraph);

        var purchaseReservation = PropertyMarketplaceReservation.AcceptPurchase(
            saleGraph,
            null,
            purchase,
            Array.Empty<PurchaseRequest>(),
            Array.Empty<RentalRequest>(),
            MarketplaceFixtures.DecidedAt);
        var rentalReservation = PropertyMarketplaceReservation.AcceptRental(
            rentGraph,
            null,
            rental,
            Array.Empty<RentalRequest>(),
            Array.Empty<PurchaseRequest>(),
            MarketplaceFixtures.DecidedAt);

        Assert.Equal(propertyId, purchaseReservation.PropertyId);
        Assert.Equal(propertyId, rentalReservation.PropertyId);
        Assert.Equal(PurchaseRequestStatus.Accepted, purchase.Status);
        Assert.Equal(RentalRequestStatus.Accepted, rental.Status);
    }
}
