using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class PropertyListingTests
{
    [Theory]
    [InlineData(ListingPurpose.Sale)]
    [InlineData(ListingPurpose.Rent)]
    public void Constructor_CreatesASeparateUnpublishedOpenIdentity(ListingPurpose purpose)
    {
        var id = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        var listing = new PropertyListing(id, propertyId, purpose, MarketplaceFixtures.CreatedAt);

        Assert.Equal(id, listing.Id);
        Assert.Equal(propertyId, listing.PropertyId);
        Assert.Equal(purpose, listing.Purpose);
        Assert.Equal(ListingPublicationStatus.Unpublished, listing.PublicationStatus);
        Assert.Equal(ListingAvailabilityStatus.Open, listing.AvailabilityStatus);
        Assert.Equal(MarketplaceFixtures.CreatedAt, listing.CreatedAt);
        Assert.Equal(MarketplaceFixtures.CreatedAt, listing.UpdatedAt);
    }

    [Fact]
    public void Constructor_AllowsSaleAndRentIdentitiesForTheSameProperty()
    {
        var propertyId = Guid.NewGuid();

        var sale = new PropertyListing(Guid.NewGuid(), propertyId, ListingPurpose.Sale, MarketplaceFixtures.CreatedAt);
        var rent = new PropertyListing(Guid.NewGuid(), propertyId, ListingPurpose.Rent, MarketplaceFixtures.CreatedAt);

        Assert.Equal(propertyId, sale.PropertyId);
        Assert.Equal(propertyId, rent.PropertyId);
        Assert.NotEqual(sale.Id, rent.Id);
        Assert.Equal(ListingPurpose.Sale, sale.Purpose);
        Assert.Equal(ListingPurpose.Rent, rent.Purpose);
    }

    [Fact]
    public void Constructor_RejectsEmptyIdsAndUndefinedPurpose()
    {
        Assert.Throws<ArgumentException>(() =>
            new PropertyListing(Guid.Empty, Guid.NewGuid(), ListingPurpose.Sale, MarketplaceFixtures.CreatedAt));
        Assert.Throws<ArgumentException>(() =>
            new PropertyListing(Guid.NewGuid(), Guid.Empty, ListingPurpose.Sale, MarketplaceFixtures.CreatedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PropertyListing(Guid.NewGuid(), Guid.NewGuid(), (ListingPurpose)0, MarketplaceFixtures.CreatedAt));
    }

    [Fact]
    public void Approval_RecordsExactSubmissionAndMakesListingAvailable()
    {
        var listing = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, publish: false);
        var submission = MarketplaceFixtures.Approve(listing, MarketplaceFixtures.PublishedAt);
        Assert.Equal(submission.Id, listing.PublishedSubmissionId);
        Assert.Equal(MarketplaceFixtures.PublishedAt, listing.PublishedAt);
        Assert.Equal(ListingPublicationStatus.Published, listing.PublicationStatus);
        Assert.Equal(ListingAvailabilityStatus.Open, listing.AvailabilityStatus);
    }

    [Fact]
    public void Publication_HasNoPublicGuidBypass()
    {
        Assert.DoesNotContain(typeof(PropertyListing).GetMethods(), m => m.Name == "Publish");
        var listing = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, publish: false);
        Assert.Throws<ArgumentException>(() => MarketplaceFixtures.Approve(listing, MarketplaceFixtures.CreatedAt.AddTicks(-1)));
    }
    [Fact]
    public void Withdraw_IsPurposeLocalAndProducesWithdrawnClosed()
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
    public void Withdraw_RejectsUnpublishedRepeatedAndEarlierTransitions()
    {
        var unpublished = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, publish: false);
        Assert.Throws<InvalidOperationException>(() => unpublished.Withdraw(MarketplaceFixtures.DecidedAt));

        var repeated = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        repeated.Withdraw(MarketplaceFixtures.DecidedAt);
        Assert.Throws<InvalidOperationException>(() => repeated.Withdraw(MarketplaceFixtures.DecidedAt.AddHours(1)));

        var earlier = MarketplaceFixtures.CreateListing(ListingPurpose.Sale);
        Assert.Throws<ArgumentException>(() => earlier.Withdraw(MarketplaceFixtures.PublishedAt.AddTicks(-1)));
    }

    [Fact]
    public void WithdrawnListing_RequiresFreshApproval()
    {
        var listing = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, publish: false);
        var old = MarketplaceFixtures.Approve(listing, MarketplaceFixtures.PublishedAt);
        listing.Withdraw(MarketplaceFixtures.DecidedAt);
        Assert.Throws<InvalidOperationException>(() => old.ApproveAndPublish(Guid.NewGuid(), listing, old,
            MarketplaceFixtures.Reviewer(old), MarketplaceFixtures.DecidedAt.AddHours(1), true,
            MarketplaceFixtures.Assessments(old, MarketplaceFixtures.DecidedAt), true, null));
    }
    [Theory]
    [InlineData(typeof(ListingPurpose), 2)]
    [InlineData(typeof(ListingSubmissionStatus), 5)]
    [InlineData(typeof(ListingPublicationStatus), 3)]
    [InlineData(typeof(ListingAvailabilityStatus), 3)]
    [InlineData(typeof(PurchaseRequestStatus), 5)]
    [InlineData(typeof(RentalRequestStatus), 5)]
    public void MarketplaceEnums_DefineOnlyTheApprovedValues(Type enumType, int expectedCount)
    {
        Assert.Equal(expectedCount, Enum.GetValues(enumType).Length);
    }
}
