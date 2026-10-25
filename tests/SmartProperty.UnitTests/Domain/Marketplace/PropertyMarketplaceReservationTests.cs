using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class PropertyMarketplaceReservationTests
{
    [Fact]
    public void AcceptPurchase_RecordsTheSinglePropertyWideReservation()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var winning = MarketplaceFixtures.CreatePurchaseRequest(sale);

        var reservation = PropertyMarketplaceReservation.AcceptPurchase(
            sale,
            null,
            winning,
            Array.Empty<PurchaseRequest>(),
            Array.Empty<RentalRequest>(),
            MarketplaceFixtures.DecidedAt);

        Assert.Equal(propertyId, reservation.PropertyId);
        Assert.Equal(ListingPurpose.Sale, reservation.ReservedPurpose);
        Assert.Equal(sale.Id, reservation.WinningListingId);
        Assert.Equal(winning.Id, reservation.WinningRequestId);
        Assert.Equal(MarketplaceFixtures.DecidedAt, reservation.ReservedAt);
        Assert.Equal(PurchaseRequestStatus.Accepted, winning.Status);
        Assert.Equal(ListingPublicationStatus.Withdrawn, sale.PublicationStatus);
        Assert.Equal(ListingAvailabilityStatus.Reserved, sale.AvailabilityStatus);
    }

    [Fact]
    public void AcceptRental_RecordsTheSinglePropertyWideReservation()
    {
        var propertyId = Guid.NewGuid();
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var winning = MarketplaceFixtures.CreateRentalRequest(rent);

        var reservation = PropertyMarketplaceReservation.AcceptRental(
            rent,
            null,
            winning,
            Array.Empty<RentalRequest>(),
            Array.Empty<PurchaseRequest>(),
            MarketplaceFixtures.DecidedAt);

        Assert.Equal(propertyId, reservation.PropertyId);
        Assert.Equal(ListingPurpose.Rent, reservation.ReservedPurpose);
        Assert.Equal(rent.Id, reservation.WinningListingId);
        Assert.Equal(winning.Id, reservation.WinningRequestId);
        Assert.Equal(RentalRequestStatus.Accepted, winning.Status);
        Assert.Equal(ListingAvailabilityStatus.Reserved, rent.AvailabilityStatus);
    }

    [Fact]
    public void AcceptPurchase_ValidatesTheWholeTransitionSetBeforeMutation()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var winning = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var unrelatedSale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var invalidCompetitor = MarketplaceFixtures.CreatePurchaseRequest(unrelatedSale);

        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptPurchase(
            sale,
            rent,
            winning,
            new[] { invalidCompetitor },
            Array.Empty<RentalRequest>(),
            MarketplaceFixtures.DecidedAt));

        Assert.Equal(PurchaseRequestStatus.Submitted, winning.Status);
        Assert.Equal(ListingPublicationStatus.Published, sale.PublicationStatus);
        Assert.Equal(ListingAvailabilityStatus.Open, sale.AvailabilityStatus);
        Assert.Equal(ListingPublicationStatus.Published, rent.PublicationStatus);
        Assert.Equal(ListingAvailabilityStatus.Open, rent.AvailabilityStatus);
        Assert.Equal(PurchaseRequestStatus.Submitted, invalidCompetitor.Status);
    }

    [Fact]
    public void AcceptRental_RejectsMismatchedOppositeListingBeforeMutation()
    {
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);
        var unrelatedSale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var winning = MarketplaceFixtures.CreateRentalRequest(rent);

        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptRental(
            rent,
            unrelatedSale,
            winning,
            Array.Empty<RentalRequest>(),
            Array.Empty<PurchaseRequest>(),
            MarketplaceFixtures.DecidedAt));

        Assert.Equal(RentalRequestStatus.Submitted, winning.Status);
        AssertPublishedOpen(rent);
        AssertPublishedOpen(unrelatedSale);
    }

    [Fact]
    public void AcceptPurchase_RejectsEarlierDecisionBeforeMutation()
    {
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var winning = MarketplaceFixtures.CreatePurchaseRequest(sale);

        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptPurchase(
            sale,
            null,
            winning,
            Array.Empty<PurchaseRequest>(),
            Array.Empty<RentalRequest>(),
            MarketplaceFixtures.RequestedAt.AddTicks(-1)));

        Assert.Equal(PurchaseRequestStatus.Submitted, winning.Status);
        AssertPublishedOpen(sale);
    }

    [Fact]
    public void Reservation_HasNoAutomaticReleaseOperation()
    {
        Assert.Null(typeof(PropertyMarketplaceReservation).GetMethod("Release"));
        Assert.Null(typeof(PropertyMarketplaceReservation).GetMethod("Cancel"));
    }

    [Fact]
    public void AcceptPurchase_MaterializesEachCompetitorCollectionExactlyOnce()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var winning = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var purchaseCompetitor = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var rentalCompetitor = MarketplaceFixtures.CreateRentalRequest(rent);
        var purchases = new SingleEnumerationReadOnlyCollection<PurchaseRequest>(purchaseCompetitor);
        var rentals = new SingleEnumerationReadOnlyCollection<RentalRequest>(rentalCompetitor);

        PropertyMarketplaceReservation.AcceptPurchase(
            sale, rent, winning, purchases, rentals, MarketplaceFixtures.DecidedAt);

        Assert.Equal(1, purchases.EnumerationCount);
        Assert.Equal(1, rentals.EnumerationCount);
        Assert.Equal(PurchaseRequestStatus.ClosedListingUnavailable, purchaseCompetitor.Status);
        Assert.Equal(RentalRequestStatus.ClosedListingUnavailable, rentalCompetitor.Status);
    }

    [Fact]
    public void AcceptRental_MaterializesEachCompetitorCollectionExactlyOnce()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var winning = MarketplaceFixtures.CreateRentalRequest(rent);
        var rentalCompetitor = MarketplaceFixtures.CreateRentalRequest(rent);
        var purchaseCompetitor = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var rentals = new SingleEnumerationReadOnlyCollection<RentalRequest>(rentalCompetitor);
        var purchases = new SingleEnumerationReadOnlyCollection<PurchaseRequest>(purchaseCompetitor);

        PropertyMarketplaceReservation.AcceptRental(
            rent, sale, winning, rentals, purchases, MarketplaceFixtures.DecidedAt);

        Assert.Equal(1, rentals.EnumerationCount);
        Assert.Equal(1, purchases.EnumerationCount);
        Assert.Equal(RentalRequestStatus.ClosedListingUnavailable, rentalCompetitor.Status);
        Assert.Equal(PurchaseRequestStatus.ClosedListingUnavailable, purchaseCompetitor.Status);
    }

    [Fact]
    public void AcceptPurchase_RejectsWrongPrimaryPurposeBeforeMutation()
    {
        var rentPrimary = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var winning = MarketplaceFixtures.CreatePurchaseRequest(sale);

        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptPurchase(
            rentPrimary, null, winning, Array.Empty<PurchaseRequest>(), Array.Empty<RentalRequest>(),
            MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(rentPrimary);
        AssertPublishedOpen(sale);
        Assert.Equal(PurchaseRequestStatus.Submitted, winning.Status);
    }

    [Fact]
    public void AcceptRental_RejectsWrongPrimaryPurposeBeforeMutation()
    {
        var salePrimary = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);
        var winning = MarketplaceFixtures.CreateRentalRequest(rent);

        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptRental(
            salePrimary, null, winning, Array.Empty<RentalRequest>(), Array.Empty<PurchaseRequest>(),
            MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(salePrimary);
        AssertPublishedOpen(rent);
        Assert.Equal(RentalRequestStatus.Submitted, winning.Status);
    }

    [Fact]
    public void AcceptPurchase_RejectsWrongWinningListingIdentityBeforeMutation()
    {
        var propertyId = Guid.NewGuid();
        var targetSale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var otherSale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var winning = MarketplaceFixtures.CreatePurchaseRequest(otherSale);

        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptPurchase(
            targetSale, null, winning, Array.Empty<PurchaseRequest>(), Array.Empty<RentalRequest>(),
            MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(targetSale);
        AssertPublishedOpen(otherSale);
        Assert.Equal(PurchaseRequestStatus.Submitted, winning.Status);
    }

    [Fact]
    public void AcceptRental_RejectsWrongWinningListingIdentityBeforeMutation()
    {
        var propertyId = Guid.NewGuid();
        var targetRent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var otherRent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var winning = MarketplaceFixtures.CreateRentalRequest(otherRent);

        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptRental(
            targetRent, null, winning, Array.Empty<RentalRequest>(), Array.Empty<PurchaseRequest>(),
            MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(targetRent);
        AssertPublishedOpen(otherRent);
        Assert.Equal(RentalRequestStatus.Submitted, winning.Status);
    }

    [Fact]
    public void AcceptPurchase_RejectsDuplicateCompetitorIdentityBeforeMutation()
    {
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var winning = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var competitor = MarketplaceFixtures.CreatePurchaseRequest(sale);

        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptPurchase(
            sale, null, winning, new[] { competitor, competitor }, Array.Empty<RentalRequest>(),
            MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(sale);
        Assert.Equal(PurchaseRequestStatus.Submitted, winning.Status);
        Assert.Equal(PurchaseRequestStatus.Submitted, competitor.Status);
    }

    [Fact]
    public void AcceptRental_RejectsDuplicateCompetitorIdentityBeforeMutation()
    {
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);
        var winning = MarketplaceFixtures.CreateRentalRequest(rent);
        var competitor = MarketplaceFixtures.CreateRentalRequest(rent);

        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptRental(
            rent, null, winning, new[] { competitor, competitor }, Array.Empty<PurchaseRequest>(),
            MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(rent);
        Assert.Equal(RentalRequestStatus.Submitted, winning.Status);
        Assert.Equal(RentalRequestStatus.Submitted, competitor.Status);
    }

    [Fact]
    public void Acceptance_RejectsNullInputsBeforeMutation()
    {
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var purchase = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);
        var rental = MarketplaceFixtures.CreateRentalRequest(rent);

        Assert.Throws<ArgumentNullException>(() => PropertyMarketplaceReservation.AcceptPurchase(
            null!, null, purchase, Array.Empty<PurchaseRequest>(), Array.Empty<RentalRequest>(),
            MarketplaceFixtures.DecidedAt));
        Assert.Throws<ArgumentNullException>(() => PropertyMarketplaceReservation.AcceptRental(
            rent, null, null!, Array.Empty<RentalRequest>(), Array.Empty<PurchaseRequest>(),
            MarketplaceFixtures.DecidedAt));
        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptPurchase(
            sale, null, purchase, new PurchaseRequest[] { null! }, Array.Empty<RentalRequest>(),
            MarketplaceFixtures.DecidedAt));
        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptRental(
            rent, null, rental, new RentalRequest[] { null! }, Array.Empty<PurchaseRequest>(),
            MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(sale);
        AssertPublishedOpen(rent);
        Assert.Equal(PurchaseRequestStatus.Submitted, purchase.Status);
        Assert.Equal(RentalRequestStatus.Submitted, rental.Status);
    }

    [Fact]
    public void AcceptPurchase_RejectsTerminalWinningRequestBeforeMutatingListingsOrCompetitors()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var winning = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var purchaseCompetitor = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var rentalCompetitor = MarketplaceFixtures.CreateRentalRequest(rent);
        winning.Reject(MarketplaceFixtures.RequestedAt.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() => PropertyMarketplaceReservation.AcceptPurchase(
            sale, rent, winning, new[] { purchaseCompetitor }, new[] { rentalCompetitor },
            MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(sale);
        AssertPublishedOpen(rent);
        Assert.Equal(PurchaseRequestStatus.Rejected, winning.Status);
        Assert.Equal(PurchaseRequestStatus.Submitted, purchaseCompetitor.Status);
        Assert.Equal(RentalRequestStatus.Submitted, rentalCompetitor.Status);
    }

    [Fact]
    public void AcceptRental_RejectsTerminalWinningRequestBeforeMutatingListingsOrCompetitors()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var winning = MarketplaceFixtures.CreateRentalRequest(rent);
        var rentalCompetitor = MarketplaceFixtures.CreateRentalRequest(rent);
        var purchaseCompetitor = MarketplaceFixtures.CreatePurchaseRequest(sale);
        winning.Withdraw(MarketplaceFixtures.RequestedAt.AddMinutes(1));

        Assert.Throws<InvalidOperationException>(() => PropertyMarketplaceReservation.AcceptRental(
            rent, sale, winning, new[] { rentalCompetitor }, new[] { purchaseCompetitor },
            MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(sale);
        AssertPublishedOpen(rent);
        Assert.Equal(RentalRequestStatus.WithdrawnByRequester, winning.Status);
        Assert.Equal(RentalRequestStatus.Submitted, rentalCompetitor.Status);
        Assert.Equal(PurchaseRequestStatus.Submitted, purchaseCompetitor.Status);
    }

    [Fact]
    public void AcceptPurchase_RejectsAlreadyReservedOppositeListingBeforeMutation()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var rentalWinner = MarketplaceFixtures.CreateRentalRequest(rent);
        PropertyMarketplaceReservation.AcceptRental(
            rent, null, rentalWinner, Array.Empty<RentalRequest>(), Array.Empty<PurchaseRequest>(),
            MarketplaceFixtures.DecidedAt);
        var purchaseWinner = MarketplaceFixtures.CreatePurchaseRequest(sale);

        Assert.Throws<InvalidOperationException>(() => PropertyMarketplaceReservation.AcceptPurchase(
            sale, rent, purchaseWinner, Array.Empty<PurchaseRequest>(), Array.Empty<RentalRequest>(),
            MarketplaceFixtures.DecidedAt.AddMinutes(1)));

        AssertPublishedOpen(sale);
        Assert.Equal(PurchaseRequestStatus.Submitted, purchaseWinner.Status);
        Assert.Equal(ListingAvailabilityStatus.Reserved, rent.AvailabilityStatus);
        Assert.Equal(RentalRequestStatus.Accepted, rentalWinner.Status);
    }

    [Fact]
    public void AcceptRental_RejectsAlreadyReservedOppositeListingBeforeMutation()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var purchaseWinner = MarketplaceFixtures.CreatePurchaseRequest(sale);
        PropertyMarketplaceReservation.AcceptPurchase(
            sale, null, purchaseWinner, Array.Empty<PurchaseRequest>(), Array.Empty<RentalRequest>(),
            MarketplaceFixtures.DecidedAt);
        var rentalWinner = MarketplaceFixtures.CreateRentalRequest(rent);

        Assert.Throws<InvalidOperationException>(() => PropertyMarketplaceReservation.AcceptRental(
            rent, sale, rentalWinner, Array.Empty<RentalRequest>(), Array.Empty<PurchaseRequest>(),
            MarketplaceFixtures.DecidedAt.AddMinutes(1)));

        AssertPublishedOpen(rent);
        Assert.Equal(RentalRequestStatus.Submitted, rentalWinner.Status);
        Assert.Equal(ListingAvailabilityStatus.Reserved, sale.AvailabilityStatus);
        Assert.Equal(PurchaseRequestStatus.Accepted, purchaseWinner.Status);
    }

    [Fact]
    public void AcceptPurchase_RejectsLateMismatchedCompetitorBeforeMutatingAnyParticipant()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var winning = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var valid = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var otherSale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var invalid = MarketplaceFixtures.CreatePurchaseRequest(otherSale);
        var rental = MarketplaceFixtures.CreateRentalRequest(rent);

        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptPurchase(
            sale, rent, winning, new[] { valid, invalid }, new[] { rental },
            MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(sale);
        AssertPublishedOpen(rent);
        AssertPublishedOpen(otherSale);
        Assert.Equal(PurchaseRequestStatus.Submitted, winning.Status);
        Assert.Equal(PurchaseRequestStatus.Submitted, valid.Status);
        Assert.Equal(PurchaseRequestStatus.Submitted, invalid.Status);
        Assert.Equal(RentalRequestStatus.Submitted, rental.Status);
    }

    [Fact]
    public void AcceptRental_RejectsLateMismatchedCompetitorBeforeMutatingAnyParticipant()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var winning = MarketplaceFixtures.CreateRentalRequest(rent);
        var valid = MarketplaceFixtures.CreateRentalRequest(rent);
        var otherRent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);
        var invalid = MarketplaceFixtures.CreateRentalRequest(otherRent);
        var purchase = MarketplaceFixtures.CreatePurchaseRequest(sale);

        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptRental(
            rent, sale, winning, new[] { valid, invalid }, new[] { purchase },
            MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(sale);
        AssertPublishedOpen(rent);
        AssertPublishedOpen(otherRent);
        Assert.Equal(RentalRequestStatus.Submitted, winning.Status);
        Assert.Equal(RentalRequestStatus.Submitted, valid.Status);
        Assert.Equal(RentalRequestStatus.Submitted, invalid.Status);
        Assert.Equal(PurchaseRequestStatus.Submitted, purchase.Status);
    }

    [Fact]
    public void Acceptance_RejectsNullCompetitorCollectionsBeforeMutation()
    {
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var purchase = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);
        var rental = MarketplaceFixtures.CreateRentalRequest(rent);

        Assert.Throws<ArgumentNullException>(() => PropertyMarketplaceReservation.AcceptPurchase(
            sale, null, purchase, null!, Array.Empty<RentalRequest>(), MarketplaceFixtures.DecidedAt));
        Assert.Throws<ArgumentNullException>(() => PropertyMarketplaceReservation.AcceptRental(
            rent, null, rental, Array.Empty<RentalRequest>(), null!, MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(sale);
        AssertPublishedOpen(rent);
        Assert.Equal(PurchaseRequestStatus.Submitted, purchase.Status);
        Assert.Equal(RentalRequestStatus.Submitted, rental.Status);
    }

    [Fact]
    public void AcceptPurchase_RejectsDistinctCompetitorsWithTheSameIdBeforeMutation()
    {
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var winning = MarketplaceFixtures.CreatePurchaseRequest(sale);
        var duplicateId = Guid.NewGuid();
        var first = MarketplaceFixtures.CreatePurchaseRequest(sale, duplicateId);
        var second = MarketplaceFixtures.CreatePurchaseRequest(sale, duplicateId);

        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptPurchase(
            sale, null, winning, new[] { first, second }, Array.Empty<RentalRequest>(),
            MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(sale);
        Assert.Equal(PurchaseRequestStatus.Submitted, winning.Status);
        Assert.Equal(PurchaseRequestStatus.Submitted, first.Status);
        Assert.Equal(PurchaseRequestStatus.Submitted, second.Status);
    }

    [Fact]
    public void AcceptRental_RejectsDistinctCompetitorsWithTheSameIdBeforeMutation()
    {
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);
        var winning = MarketplaceFixtures.CreateRentalRequest(rent);
        var duplicateId = Guid.NewGuid();
        var first = MarketplaceFixtures.CreateRentalRequest(rent, duplicateId);
        var second = MarketplaceFixtures.CreateRentalRequest(rent, duplicateId);

        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptRental(
            rent, null, winning, new[] { first, second }, Array.Empty<PurchaseRequest>(),
            MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(rent);
        Assert.Equal(RentalRequestStatus.Submitted, winning.Status);
        Assert.Equal(RentalRequestStatus.Submitted, first.Status);
        Assert.Equal(RentalRequestStatus.Submitted, second.Status);
    }

    [Fact]
    public void AcceptPurchase_RejectsWinningRequestAmongCompetitorsBeforeMutation()
    {
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var winning = MarketplaceFixtures.CreatePurchaseRequest(sale);

        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptPurchase(
            sale, null, winning, new[] { winning }, Array.Empty<RentalRequest>(),
            MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(sale);
        Assert.Equal(PurchaseRequestStatus.Submitted, winning.Status);
    }

    [Fact]
    public void AcceptRental_RejectsWinningRequestAmongCompetitorsBeforeMutation()
    {
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);
        var winning = MarketplaceFixtures.CreateRentalRequest(rent);

        Assert.Throws<ArgumentException>(() => PropertyMarketplaceReservation.AcceptRental(
            rent, null, winning, new[] { winning }, Array.Empty<PurchaseRequest>(),
            MarketplaceFixtures.DecidedAt));

        AssertPublishedOpen(rent);
        Assert.Equal(RentalRequestStatus.Submitted, winning.Status);
    }

    private static void AssertPublishedOpen(PropertyListing listing)
    {
        Assert.Equal(ListingPublicationStatus.Published, listing.PublicationStatus);
        Assert.Equal(ListingAvailabilityStatus.Open, listing.AvailabilityStatus);
    }

    private sealed class SingleEnumerationReadOnlyCollection<T> : IReadOnlyCollection<T>
    {
        private readonly T[] _items;

        public SingleEnumerationReadOnlyCollection(params T[] items)
        {
            _items = items;
        }

        public int Count => _items.Length;
        public int EnumerationCount { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationCount++;
            if (EnumerationCount > 1)
            {
                throw new InvalidOperationException("The source collection was enumerated more than once.");
            }

            return ((IEnumerable<T>)_items).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
