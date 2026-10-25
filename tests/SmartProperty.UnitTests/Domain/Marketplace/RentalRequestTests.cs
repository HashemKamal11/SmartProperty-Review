using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class RentalRequestTests
{
    [Fact]
    public void Constructor_CreatesSubmittedRequestForPublishedOpenRentListing()
    {
        var listing = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);
        var id = Guid.NewGuid();
        var renterId = Guid.NewGuid();

        var request = new RentalRequest(id, listing, renterId, MarketplaceFixtures.RequestedAt);

        Assert.Equal(id, request.Id);
        Assert.Equal(listing.Id, request.ListingId);
        Assert.Equal(listing.PropertyId, request.PropertyId);
        Assert.Equal(renterId, request.RenterUserId);
        Assert.Equal(RentalRequestStatus.Submitted, request.Status);
        Assert.Equal(MarketplaceFixtures.RequestedAt, request.SubmittedAt);
        Assert.Equal(MarketplaceFixtures.RequestedAt, request.UpdatedAt);
        Assert.Null(request.DecidedAt);
    }

    [Fact]
    public void Constructor_RejectsInvalidIdentityPurposeStateAndTime()
    {
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var unpublished = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, publish: false);

        Assert.Throws<ArgumentException>(() =>
            new RentalRequest(Guid.Empty, rent, Guid.NewGuid(), MarketplaceFixtures.RequestedAt));
        Assert.Throws<ArgumentException>(() =>
            new RentalRequest(Guid.NewGuid(), rent, Guid.Empty, MarketplaceFixtures.RequestedAt));
        Assert.Throws<ArgumentException>(() =>
            new RentalRequest(Guid.NewGuid(), sale, Guid.NewGuid(), MarketplaceFixtures.RequestedAt));
        Assert.Throws<InvalidOperationException>(() =>
            new RentalRequest(Guid.NewGuid(), unpublished, Guid.NewGuid(), MarketplaceFixtures.RequestedAt));
        Assert.Throws<ArgumentException>(() =>
            new RentalRequest(Guid.NewGuid(), rent, Guid.NewGuid(), MarketplaceFixtures.PublishedAt.AddTicks(-1)));
    }

    [Fact]
    public void Reject_TransitionsSubmittedRequestToTerminalRejected()
    {
        var request = MarketplaceFixtures.CreateRentalRequest(
            MarketplaceFixtures.CreateListing(ListingPurpose.Rent));

        request.Reject(MarketplaceFixtures.DecidedAt);

        Assert.Equal(RentalRequestStatus.Rejected, request.Status);
        Assert.Equal(MarketplaceFixtures.DecidedAt, request.DecidedAt);
        Assert.Equal(MarketplaceFixtures.DecidedAt, request.UpdatedAt);
        Assert.Throws<InvalidOperationException>(() =>
            request.Withdraw(MarketplaceFixtures.DecidedAt.AddMinutes(1)));
    }

    [Fact]
    public void Withdraw_TransitionsSubmittedRequestToTerminalRequesterWithdrawal()
    {
        var request = MarketplaceFixtures.CreateRentalRequest(
            MarketplaceFixtures.CreateListing(ListingPurpose.Rent));

        request.Withdraw(MarketplaceFixtures.DecidedAt);

        Assert.Equal(RentalRequestStatus.WithdrawnByRequester, request.Status);
        Assert.Equal(MarketplaceFixtures.DecidedAt, request.DecidedAt);
        Assert.Throws<InvalidOperationException>(() =>
            request.Reject(MarketplaceFixtures.DecidedAt.AddMinutes(1)));
    }

    [Fact]
    public void PublicTransitions_RejectEarlierOrRepeatedChanges()
    {
        var earlier = MarketplaceFixtures.CreateRentalRequest(
            MarketplaceFixtures.CreateListing(ListingPurpose.Rent));
        Assert.Throws<ArgumentException>(() =>
            earlier.Reject(MarketplaceFixtures.RequestedAt.AddTicks(-1)));

        var repeated = MarketplaceFixtures.CreateRentalRequest(
            MarketplaceFixtures.CreateListing(ListingPurpose.Rent));
        repeated.Reject(MarketplaceFixtures.DecidedAt);
        Assert.Throws<InvalidOperationException>(() =>
            repeated.Reject(MarketplaceFixtures.DecidedAt.AddMinutes(1)));
    }
}
