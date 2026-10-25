using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class PurchaseRequestTests
{
    [Fact]
    public void Constructor_CreatesSubmittedRequestForPublishedOpenSaleListing()
    {
        var listing = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var id = Guid.NewGuid();
        var buyerId = Guid.NewGuid();

        var request = new PurchaseRequest(id, listing, buyerId, MarketplaceFixtures.RequestedAt);

        Assert.Equal(id, request.Id);
        Assert.Equal(listing.Id, request.ListingId);
        Assert.Equal(listing.PropertyId, request.PropertyId);
        Assert.Equal(buyerId, request.BuyerUserId);
        Assert.Equal(PurchaseRequestStatus.Submitted, request.Status);
        Assert.Equal(MarketplaceFixtures.RequestedAt, request.SubmittedAt);
        Assert.Equal(MarketplaceFixtures.RequestedAt, request.UpdatedAt);
        Assert.Null(request.DecidedAt);
    }

    [Fact]
    public void Constructor_RejectsInvalidIdentityPurposeStateAndTime()
    {
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent);
        var unpublished = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, publish: false);

        Assert.Throws<ArgumentException>(() =>
            new PurchaseRequest(Guid.Empty, sale, Guid.NewGuid(), MarketplaceFixtures.RequestedAt));
        Assert.Throws<ArgumentException>(() =>
            new PurchaseRequest(Guid.NewGuid(), sale, Guid.Empty, MarketplaceFixtures.RequestedAt));
        Assert.Throws<ArgumentException>(() =>
            new PurchaseRequest(Guid.NewGuid(), rent, Guid.NewGuid(), MarketplaceFixtures.RequestedAt));
        Assert.Throws<InvalidOperationException>(() =>
            new PurchaseRequest(Guid.NewGuid(), unpublished, Guid.NewGuid(), MarketplaceFixtures.RequestedAt));
        Assert.Throws<ArgumentException>(() =>
            new PurchaseRequest(Guid.NewGuid(), sale, Guid.NewGuid(), MarketplaceFixtures.PublishedAt.AddTicks(-1)));
    }

    [Fact]
    public void Reject_TransitionsSubmittedRequestToTerminalRejected()
    {
        var request = MarketplaceFixtures.CreatePurchaseRequest(
            MarketplaceFixtures.CreateListing(ListingPurpose.Sale));

        request.Reject(MarketplaceFixtures.DecidedAt);

        Assert.Equal(PurchaseRequestStatus.Rejected, request.Status);
        Assert.Equal(MarketplaceFixtures.DecidedAt, request.DecidedAt);
        Assert.Equal(MarketplaceFixtures.DecidedAt, request.UpdatedAt);
        Assert.Throws<InvalidOperationException>(() =>
            request.Withdraw(MarketplaceFixtures.DecidedAt.AddMinutes(1)));
    }

    [Fact]
    public void Withdraw_TransitionsSubmittedRequestToTerminalRequesterWithdrawal()
    {
        var request = MarketplaceFixtures.CreatePurchaseRequest(
            MarketplaceFixtures.CreateListing(ListingPurpose.Sale));

        request.Withdraw(MarketplaceFixtures.DecidedAt);

        Assert.Equal(PurchaseRequestStatus.WithdrawnByRequester, request.Status);
        Assert.Equal(MarketplaceFixtures.DecidedAt, request.DecidedAt);
        Assert.Throws<InvalidOperationException>(() =>
            request.Reject(MarketplaceFixtures.DecidedAt.AddMinutes(1)));
    }

    [Fact]
    public void PublicTransitions_RejectEarlierOrRepeatedChanges()
    {
        var earlier = MarketplaceFixtures.CreatePurchaseRequest(
            MarketplaceFixtures.CreateListing(ListingPurpose.Sale));
        Assert.Throws<ArgumentException>(() =>
            earlier.Reject(MarketplaceFixtures.RequestedAt.AddTicks(-1)));

        var repeated = MarketplaceFixtures.CreatePurchaseRequest(
            MarketplaceFixtures.CreateListing(ListingPurpose.Sale));
        repeated.Reject(MarketplaceFixtures.DecidedAt);
        Assert.Throws<InvalidOperationException>(() =>
            repeated.Reject(MarketplaceFixtures.DecidedAt.AddMinutes(1)));
    }
}
