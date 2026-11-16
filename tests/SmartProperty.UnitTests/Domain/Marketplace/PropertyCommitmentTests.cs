using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

/// <summary>
/// Dated requests, the commitments they become, and what accepting one does to the property's advertisements and
/// other requests. These are in-memory transitions; the database rules (overlap, single sale) are proven in the
/// persistence tests.
/// </summary>
public sealed class PropertyCommitmentTests
{
    private static readonly DateTimeOffset At = MarketplaceFixtures.DecidedAt;
    private static readonly DateTimeOffset Deadline = At.AddHours(72);
    private static readonly Guid Owner = Guid.NewGuid();

    // ---- dated requests

    [Fact]
    public void ADatedRentalRequestCarriesThePricingStayAndTermsItWasMadeAgainst()
    {
        var listing = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);

        var request = Rental(listing, RentPeriod.Weekly, new DateOnly(2027, 6, 1), new DateOnly(2027, 6, 15));

        Assert.True(request.IsDated);
        Assert.Equal(RentPeriod.Weekly, request.PricingPeriod);
        Assert.Equal(6_000m, request.Amount);
        Assert.Equal("EGP", request.Currency);
        Assert.Equal(new DateOnly(2027, 6, 1), request.StartDate);
        Assert.Equal(new DateOnly(2027, 6, 15), request.EndDate);
        Assert.Equal(new TimeOnly(14, 0), request.CheckInTime);
        Assert.Equal("Africa/Cairo", request.TimeZoneId);
        Assert.Equal(listing.PublishedSubmissionId, request.SubmissionId);
    }

    [Fact]
    public void ARentalMustEndAfterItStartsAndAgainstTheCurrentApprovedVersion()
    {
        var listing = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);

        Assert.Throws<ArgumentException>(() => Rental(listing, RentPeriod.Daily, new DateOnly(2027, 6, 5), new DateOnly(2027, 6, 5)));
        Assert.Throws<ArgumentException>(() => Rental(listing, RentPeriod.Daily, new DateOnly(2027, 6, 5), new DateOnly(2027, 6, 1)));
        Assert.Throws<ArgumentException>(() => Rental(listing, RentPeriod.Daily, new DateOnly(2027, 6, 1), new DateOnly(2040, 6, 1)));
        Assert.Throws<ArgumentException>(() => new RentalRequest(
            Guid.NewGuid(), listing, Guid.NewGuid(), MarketplaceFixtures.RequestedAt, Guid.NewGuid(),
            new RentPricingOption(RentPeriod.Daily, 100m, "EGP"), new DateOnly(2027, 6, 1), new DateOnly(2027, 6, 2),
            new TimeOnly(14, 0), new TimeOnly(11, 0), "Africa/Cairo", null));
    }

    [Fact]
    public void ARentalNeedsARecognisedTimeZoneAndAShortMessage()
    {
        var listing = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);

        Assert.Throws<ArgumentException>(() => Rental(listing, RentPeriod.Daily, new DateOnly(2027, 6, 1), new DateOnly(2027, 6, 2), zone: "Mars/Olympus"));
        Assert.Throws<ArgumentException>(() => Rental(listing, RentPeriod.Daily, new DateOnly(2027, 6, 1), new DateOnly(2027, 6, 2), message: new string('x', 1001)));
    }

    [Fact]
    public void ARequestThatPredatesDatedRentalsHasNoDates()
    {
        var request = MarketplaceFixtures.CreateRentalRequest(MarketplaceFixtures.CreateListing(ListingPurpose.Rent));

        Assert.False(request.IsDated);
        Assert.Null(request.SubmissionId);
        Assert.Null(request.StartDate);
    }

    [Fact]
    public void APurchaseOfferNeedsAnAmountAboveZeroInAThreeLetterCurrency()
    {
        var listing = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);

        var ok = new PurchaseRequest(Guid.NewGuid(), listing, Guid.NewGuid(), MarketplaceFixtures.RequestedAt,
            listing.PublishedSubmissionId!.Value, 900_000m, "EGP", "  Interested  ");

        Assert.Equal(900_000m, ok.OfferedAmount);
        Assert.Equal("Interested", ok.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PurchaseRequest(Guid.NewGuid(), listing, Guid.NewGuid(),
            MarketplaceFixtures.RequestedAt, listing.PublishedSubmissionId.Value, 0m, "EGP", null));
        Assert.Throws<ArgumentException>(() => new PurchaseRequest(Guid.NewGuid(), listing, Guid.NewGuid(),
            MarketplaceFixtures.RequestedAt, listing.PublishedSubmissionId.Value, 10m, "egp", null));
    }

    // ---- accepting a rental

    [Fact]
    public void AcceptingARentalCommitsItsDatesWithAFixedDeadlineAndClosesBothAdvertisements()
    {
        var propertyId = Guid.NewGuid();
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var winner = Rental(rent, RentPeriod.Monthly, new DateOnly(2027, 6, 1), new DateOnly(2027, 9, 1));

        var commitment = CommitmentAcceptance.AcceptRental(rent, sale, winner, [], [], Owner, At, Deadline);

        Assert.Equal(CommitmentKind.Rental, commitment.Kind);
        Assert.Equal(CommitmentStatus.PendingSignature, commitment.Status);
        Assert.Equal(winner.Id, commitment.RentalRequestId);
        Assert.Equal(winner.RenterUserId, commitment.CustomerUserId);
        Assert.Equal(Owner, commitment.OwnerUserId);
        Assert.Equal(new DateOnly(2027, 6, 1), commitment.StartDate);
        Assert.Equal(Deadline, commitment.SigningDeadline);
        Assert.Equal(RentalRequestStatus.Accepted, winner.Status);
        Assert.Equal(ListingPublicationStatus.Withdrawn, rent.PublicationStatus);
        Assert.Equal(ListingAvailabilityStatus.Closed, rent.AvailabilityStatus);
        Assert.Equal(ListingPublicationStatus.Withdrawn, sale.PublicationStatus);
        Assert.False(rent.ClosedByReservation);
    }

    [Fact]
    public void AcceptingClosesEveryOtherOpenRequestOfEitherKindAndKeepsTheirHistory()
    {
        var propertyId = Guid.NewGuid();
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var winner = Rental(rent, RentPeriod.Daily, new DateOnly(2027, 6, 1), new DateOnly(2027, 6, 5));
        var otherRental = Rental(rent, RentPeriod.Daily, new DateOnly(2027, 6, 3), new DateOnly(2027, 6, 8));
        var purchase = MarketplaceFixtures.CreatePurchaseRequest(sale);

        CommitmentAcceptance.AcceptRental(rent, sale, winner, [otherRental], [purchase], Owner, At, Deadline);

        Assert.Equal(RentalRequestStatus.ClosedListingUnavailable, otherRental.Status);
        Assert.Equal(PurchaseRequestStatus.ClosedListingUnavailable, purchase.Status);
        Assert.Equal(At, otherRental.DecidedAt);
        Assert.Equal(otherRental.StartDate, new DateOnly(2027, 6, 3));
    }

    [Fact]
    public void AnAdvertisementThatIsNotPublishedIsLeftAsItIs()
    {
        var propertyId = Guid.NewGuid();
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var draftSale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId, publish: false);
        var winner = Rental(rent, RentPeriod.Daily, new DateOnly(2027, 6, 1), new DateOnly(2027, 6, 5));

        CommitmentAcceptance.AcceptRental(rent, draftSale, winner, [], [], Owner, At, Deadline);

        Assert.Equal(ListingPublicationStatus.Unpublished, draftSale.PublicationStatus);
        Assert.Equal(ListingAvailabilityStatus.Open, draftSale.AvailabilityStatus);
    }

    [Fact]
    public void AnUndatedRequestCannotBeCommittedToAndNothingChanges()
    {
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);
        var legacy = MarketplaceFixtures.CreateRentalRequest(rent);

        Assert.Throws<InvalidOperationException>(() =>
            CommitmentAcceptance.AcceptRental(rent, null, legacy, [], [], Owner, At, Deadline));

        Assert.Equal(RentalRequestStatus.Submitted, legacy.Status);
        Assert.Equal(ListingPublicationStatus.Published, rent.PublicationStatus);
    }

    [Fact]
    public void TheWinnerCannotBeItsOwnCompetitorAndTheDeadlineMustFollowTheAcceptance()
    {
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);
        var winner = Rental(rent, RentPeriod.Daily, new DateOnly(2027, 6, 1), new DateOnly(2027, 6, 5));

        Assert.Throws<ArgumentException>(() => CommitmentAcceptance.AcceptRental(rent, null, winner, [winner], [], Owner, At, Deadline));
        Assert.Throws<ArgumentException>(() => CommitmentAcceptance.AcceptRental(rent, null, winner, [], [], Owner, At, At));
        Assert.Equal(RentalRequestStatus.Submitted, winner.Status);
    }

    // ---- accepting a sale

    [Fact]
    public void AcceptingASaleCommitsItWithoutDatesAndClosesTheAdvertisements()
    {
        var propertyId = Guid.NewGuid();
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var winner = Purchase(sale, 950_000m);

        var commitment = CommitmentAcceptance.AcceptSale(sale, rent, winner, [], [], Owner, At, Deadline);

        Assert.Equal(CommitmentKind.Sale, commitment.Kind);
        Assert.Equal(950_000m, commitment.Amount);
        Assert.Null(commitment.StartDate);
        Assert.Equal(PurchaseRequestStatus.Accepted, winner.Status);
        Assert.Equal(ListingPublicationStatus.Withdrawn, sale.PublicationStatus);
        Assert.Equal(ListingPublicationStatus.Withdrawn, rent.PublicationStatus);
    }

    // ---- the life of a commitment

    [Fact]
    public void ARentalIsSignedThenOccupiedThenCompleted()
    {
        var commitment = AcceptedRental(RentPeriod.Monthly);

        commitment.ConfirmSigned(At.AddHours(1));
        commitment.ConfirmHandover(At.AddHours(2));

        Assert.Equal(CommitmentStatus.Occupied, commitment.Status);
        Assert.True(commitment.IsActiveAt(At.AddDays(30)));

        commitment.ConfirmReturn(At.AddHours(3));

        Assert.Equal(CommitmentStatus.Completed, commitment.Status);
        Assert.False(commitment.IsActiveAt(At.AddHours(4)));
        Assert.Equal(At.AddHours(3), commitment.CompletedAt);
    }

    [Fact]
    public void ASaleIsCompletedByTheHandoverBecauseOwnershipTransferIsOutsideTheModel()
    {
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var commitment = CommitmentAcceptance.AcceptSale(sale, null, Purchase(sale, 1m), [], [], Owner, At, Deadline);

        commitment.ConfirmSigned(At.AddHours(1));
        commitment.ConfirmHandover(At.AddHours(2));

        Assert.Equal(CommitmentStatus.Completed, commitment.Status);
        Assert.Throws<InvalidOperationException>(() => commitment.ConfirmReturn(At.AddHours(3)));
    }

    [Fact]
    public void StepsCannotBeSkippedOrRepeated()
    {
        var commitment = AcceptedRental(RentPeriod.Monthly);

        Assert.Throws<InvalidOperationException>(() => commitment.ConfirmHandover(At.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() => commitment.ConfirmReturn(At.AddHours(1)));

        commitment.ConfirmSigned(At.AddHours(1));

        Assert.Throws<InvalidOperationException>(() => commitment.ConfirmSigned(At.AddHours(2)));
        Assert.Throws<InvalidOperationException>(() => commitment.ConfirmReturn(At.AddHours(2)));
    }

    [Fact]
    public void AnUnsignedCommitmentPastItsDeadlineStopsCountingCannotBeSignedAndIsExpiredOnTouch()
    {
        var commitment = AcceptedRental(RentPeriod.Daily);

        Assert.True(commitment.IsActiveAt(Deadline.AddTicks(-1)));
        Assert.False(commitment.IsActiveAt(Deadline));
        Assert.True(commitment.IsOverdue(Deadline));
        Assert.Throws<InvalidOperationException>(() => commitment.ConfirmSigned(Deadline));

        commitment.Expire(Deadline.AddMinutes(1));

        Assert.Equal(CommitmentStatus.Expired, commitment.Status);
        Assert.NotNull(commitment.ExpiredAt);
    }

    [Fact]
    public void ASignedCommitmentIsNeverExpiredByTheClock()
    {
        var commitment = AcceptedRental(RentPeriod.Daily);
        commitment.ConfirmSigned(At.AddHours(1));

        commitment.Expire(Deadline.AddDays(365));

        Assert.Equal(CommitmentStatus.Signed, commitment.Status);
        Assert.True(commitment.IsActiveAt(Deadline.AddDays(365)));
    }

    [Fact]
    public void OnlyDailyAndWeeklyRentalsAreShortStaysThatBlockASale()
    {
        Assert.True(AcceptedRental(RentPeriod.Daily).IsShortStay);
        Assert.True(AcceptedRental(RentPeriod.Weekly).IsShortStay);
        Assert.False(AcceptedRental(RentPeriod.Monthly).IsShortStay);
        Assert.False(AcceptedRental(RentPeriod.Yearly).IsShortStay);

        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        Assert.False(CommitmentAcceptance.AcceptSale(sale, null, Purchase(sale, 1m), [], [], Owner, At, Deadline).IsShortStay);
    }

    [Fact]
    public void StaysAreHalfOpenSoAnotherMayBeginOnTheCheckOutDay()
    {
        var commitment = AcceptedRental(RentPeriod.Daily, new DateOnly(2027, 6, 1), new DateOnly(2027, 6, 10));

        Assert.True(commitment.Overlaps(new DateOnly(2027, 6, 9), new DateOnly(2027, 6, 12)));
        Assert.True(commitment.Overlaps(new DateOnly(2027, 5, 25), new DateOnly(2027, 6, 2)));
        Assert.False(commitment.Overlaps(new DateOnly(2027, 6, 10), new DateOnly(2027, 6, 15)));
        Assert.False(commitment.Overlaps(new DateOnly(2027, 5, 20), new DateOnly(2027, 6, 1)));
    }

    // ---- settings

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(721)]
    public void TheSigningWindowIsOneToSevenHundredTwentyHours(int hours)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarketplaceSettings(hours, Guid.NewGuid(), At));
    }

    [Fact]
    public void TheSigningWindowRecordsWhoChangedItAndNeedsAnAdministrator()
    {
        var admin = Guid.NewGuid();
        var settings = new MarketplaceSettings(48, admin, At);

        settings.ChangeSigningWindow(96, admin, At.AddDays(1));

        Assert.Equal(96, settings.SigningWindowHours);
        Assert.Equal(1, settings.Id);
        Assert.Throws<ArgumentException>(() => settings.ChangeSigningWindow(10, Guid.Empty, At));
    }

    // ---- relisting

    [Fact]
    public void AcceptingRemembersTheCommitmentOnEveryListingItClosedAndPublishingOrWithdrawingForgetsIt()
    {
        var propertyId = Guid.NewGuid();
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, propertyId);
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, propertyId);
        var winner = Rental(rent, RentPeriod.Daily, new DateOnly(2027, 6, 1), new DateOnly(2027, 6, 5));

        var commitment = CommitmentAcceptance.AcceptRental(rent, sale, winner, [], [], Owner, At, Deadline);

        Assert.Equal(commitment.Id, rent.ClosedByCommitmentId);
        Assert.Equal(commitment.Id, sale.ClosedByCommitmentId);
        Assert.True(rent.CanBeRelisted);
        Assert.False(rent.RelistAutomatically);

        var open = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        open.Withdraw(At);

        Assert.Null(open.ClosedByCommitmentId);
        Assert.True(open.CanBeRelisted);
        Assert.False(MarketplaceFixtures.CreateListing(ListingPurpose.Sale).CanBeRelisted);
    }

    [Fact]
    public void ACommitmentAllowsRelistingOnceItsSignatureIsOverdueOrItsRentalIsReturnedButNeverAfterASale()
    {
        var pending = AcceptedRental(RentPeriod.Daily);
        var returned = AcceptedRental(RentPeriod.Daily);
        returned.ConfirmSigned(At.AddHours(1));
        returned.ConfirmHandover(At.AddHours(2));

        Assert.False(pending.AllowsRelistAt(Deadline.AddTicks(-1)));
        Assert.True(pending.AllowsRelistAt(Deadline));
        Assert.False(returned.AllowsRelistAt(Deadline.AddDays(30)));

        returned.ConfirmReturn(At.AddHours(3));
        pending.Expire(Deadline);

        Assert.True(returned.AllowsRelistAt(At.AddHours(3)));
        Assert.True(pending.AllowsRelistAt(Deadline));

        var saleListing = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var sold = CommitmentAcceptance.AcceptSale(saleListing, null, Purchase(saleListing, 900_000m), [], [], Owner, At, Deadline);
        sold.ConfirmSigned(At.AddHours(1));
        sold.ConfirmHandover(At.AddHours(2));

        Assert.Equal(CommitmentStatus.Completed, sold.Status);
        Assert.False(sold.AllowsRelistAt(At.AddDays(30)));
    }

    // ---- helpers

    private static RentalRequest Rental(
        PropertyListing listing, RentPeriod period, DateOnly start, DateOnly end, string zone = "Africa/Cairo", string? message = null)
    {
        var amount = period switch { RentPeriod.Daily => 1_000m, RentPeriod.Weekly => 6_000m, RentPeriod.Monthly => 20_000m, _ => 200_000m };

        return new RentalRequest(
            Guid.NewGuid(), listing, Guid.NewGuid(), MarketplaceFixtures.RequestedAt, listing.PublishedSubmissionId!.Value,
            new RentPricingOption(period, amount, "EGP"), start, end, new TimeOnly(14, 0), new TimeOnly(11, 0), zone, message);
    }

    private static PurchaseRequest Purchase(PropertyListing listing, decimal amount) =>
        new(Guid.NewGuid(), listing, Guid.NewGuid(), MarketplaceFixtures.RequestedAt, listing.PublishedSubmissionId!.Value, amount, "EGP", null);

    private static PropertyCommitment AcceptedRental(RentPeriod period, DateOnly? start = null, DateOnly? end = null)
    {
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);
        var request = Rental(rent, period, start ?? new DateOnly(2027, 6, 1), end ?? new DateOnly(2027, 6, 10));

        return CommitmentAcceptance.AcceptRental(rent, null, request, [], [], Owner, At, Deadline);
    }
}
