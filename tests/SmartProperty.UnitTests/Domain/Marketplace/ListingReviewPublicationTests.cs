using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Marketplace;

public sealed class ListingReviewPublicationTests
{
    [Theory]
    [InlineData(ListingPurpose.Sale)]
    [InlineData(ListingPurpose.Rent)]
    public void RevisionApprovalSwitchesPointerAndPreservesHistory(ListingPurpose purpose)
    {
        var listing = MarketplaceFixtures.CreateListing(purpose, publish: false);
        var first = MarketplaceFixtures.Approve(listing, MarketplaceFixtures.PublishedAt);
        var decision = first.ReviewDecision;
        var content = first.Content;
        var evidence = first.Evidence;
        var second = MarketplaceFixtures.Pending(listing, MarketplaceFixtures.RequestedAt, first);
        Assert.Equal(first.Id, listing.PublishedSubmissionId);
        Approve(second, listing, first);
        Assert.Equal(second.Id, listing.PublishedSubmissionId);
        Assert.Equal(MarketplaceFixtures.DecidedAt, listing.PublishedAt);
        Assert.Equal(ListingSubmissionStatus.Superseded, first.Status);
        Assert.Equal(ListingSubmissionStatus.Approved, second.Status);
        Assert.Same(decision, first.ReviewDecision);
        Assert.True(first.ReviewDecision!.Approved);
        Assert.Same(content, first.Content);
        Assert.Same(evidence, first.Evidence);
    }

    [Fact]
    public void RejectionPreservesPublicationAndRequiresNoScanInput()
    {
        var listing = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, publish: false);
        var first = MarketplaceFixtures.Approve(listing, MarketplaceFixtures.PublishedAt);
        var next = MarketplaceFixtures.Pending(listing, MarketplaceFixtures.RequestedAt, first);
        next.Reject(Guid.NewGuid(), MarketplaceFixtures.Reviewer(next), "Authority is insufficient", MarketplaceFixtures.DecidedAt);
        Assert.Equal(first.Id, listing.PublishedSubmissionId);
        Assert.Equal(MarketplaceFixtures.PublishedAt, listing.PublishedAt);
        Assert.Equal(ListingSubmissionStatus.Approved, first.Status);
        Assert.False(next.ReviewDecision!.Approved);
        Assert.Equal("Authority is insufficient", next.ReviewDecision.Reason);
        Assert.Throws<InvalidOperationException>(() => next.Reject(Guid.NewGuid(), MarketplaceFixtures.Reviewer(next), "Again", MarketplaceFixtures.DecidedAt));
    }

    [Theory]
    [InlineData("unknown_availability")]
    [InlineData("unavailable")]
    [InlineData("unknown_legal")]
    [InlineData("self")]
    [InlineData("owner")]
    [InlineData("unknown_conflict")]
    [InlineData("organization_conflict")]
    [InlineData("unknown_identity")]
    [InlineData("not_admin")]
    [InlineData("wrong_submitter")]
    [InlineData("wrong_owner")]
    [InlineData("wrong_workspace")]
    [InlineData("missing_current")]
    [InlineData("wrong_listing")]
    [InlineData("missing_scan")]
    [InlineData("failed_scan")]
    [InlineData("early")]
    [InlineData("empty_decision")]
    public void FailedApprovalPreservesEveryParticipant(string fault)
    {
        var listing = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, publish: false);
        var first = MarketplaceFixtures.Approve(listing, MarketplaceFixtures.PublishedAt);
        var next = MarketplaceFixtures.Pending(listing, MarketplaceFixtures.RequestedAt, first);
        var oldDecision = first.ReviewDecision;
        var reviewer = new ListingReviewContext(
            fault == "self" ? next.Authority.ActingUserId : fault == "owner" ? next.Authority.DeclaredOwner.LinkedUserId!.Value : Guid.NewGuid(),
            fault == "wrong_submitter" ? Guid.NewGuid() : next.Authority.ActingUserId,
            fault == "wrong_owner" ? new ListingOwnerIdentity(ListingOwnerKind.Organization, Guid.NewGuid()) : next.Authority.DeclaredOwner,
            fault == "wrong_workspace" ? Guid.NewGuid() : null,
            fault == "unknown_identity" ? null : true, fault != "not_admin",
            fault == "wrong_workspace" ? false : null,
            fault == "unknown_conflict" ? null : fault == "organization_conflict");
        var scans = fault == "missing_scan" ? Array.Empty<ListingEvidenceAssessment>() :
            new[] { new ListingEvidenceAssessment(Guid.NewGuid(), next.Evidence[0], fault == "failed_scan" ? EvidenceScanStatus.Failed : EvidenceScanStatus.Clean, MarketplaceFixtures.RequestedAt) };
        Assert.ThrowsAny<Exception>(() => next.ApproveAndPublish(fault == "empty_decision" ? Guid.Empty : Guid.NewGuid(),
            fault == "wrong_listing" ? MarketplaceFixtures.CreateListing(ListingPurpose.Rent, publish: false) : listing,
            fault == "missing_current" ? null : first, reviewer,
            fault == "early" ? MarketplaceFixtures.CreatedAt : MarketplaceFixtures.DecidedAt,
            fault == "unknown_availability" ? null : fault != "unavailable", scans,
            fault == "unknown_legal" ? null : true, null));
        Assert.Equal(first.Id, listing.PublishedSubmissionId);
        Assert.Equal(MarketplaceFixtures.PublishedAt, listing.UpdatedAt);
        Assert.Equal(ListingSubmissionStatus.Approved, first.Status);
        Assert.Same(oldDecision, first.ReviewDecision);
        Assert.Equal(ListingSubmissionStatus.PendingReview, next.Status);
        Assert.Equal(MarketplaceFixtures.RequestedAt, next.UpdatedAt);
        Assert.Null(next.ReviewDecision);
    }

    [Fact]
    public void WithdrawalRequiresNewSubmissionAndRetainsWithdrawalHistory()
    {
        var listing = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, publish: false);
        var first = MarketplaceFixtures.Approve(listing, MarketplaceFixtures.PublishedAt);
        var stale = MarketplaceFixtures.Pending(listing, MarketplaceFixtures.RequestedAt, first);
        listing.Withdraw(MarketplaceFixtures.DecidedAt);
        Assert.Throws<InvalidOperationException>(() => Approve(stale, listing, first));
        var fresh = MarketplaceFixtures.Pending(listing, MarketplaceFixtures.DecidedAt.AddMinutes(1), first);
        fresh.ApproveAndPublish(Guid.NewGuid(), listing, first, MarketplaceFixtures.Reviewer(fresh),
            MarketplaceFixtures.DecidedAt.AddMinutes(2), true, MarketplaceFixtures.Assessments(fresh, MarketplaceFixtures.DecidedAt.AddMinutes(1)), true, null);
        Assert.Equal(fresh.Id, listing.PublishedSubmissionId);
        Assert.Equal(MarketplaceFixtures.DecidedAt, listing.WithdrawnAt);
        Assert.Equal(ListingAvailabilityStatus.Open, listing.AvailabilityStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReservationClosesOppositeEvenWhenPreviouslyWithdrawn(bool withdrawn)
    {
        var sale = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, publish: false);
        var rent = MarketplaceFixtures.CreateListing(ListingPurpose.Rent, sale.PropertyId, publish: false);
        var saleVersion = MarketplaceFixtures.Approve(sale, MarketplaceFixtures.PublishedAt);
        var rentVersion = MarketplaceFixtures.Approve(rent, MarketplaceFixtures.PublishedAt);
        if (withdrawn) rent.Withdraw(MarketplaceFixtures.RequestedAt);
        var winner = MarketplaceFixtures.CreatePurchaseRequest(sale);
        PropertyMarketplaceReservation.AcceptPurchase(sale, rent, winner, [], [], MarketplaceFixtures.DecidedAt);
        Assert.True(sale.ClosedByReservation);
        Assert.True(rent.ClosedByReservation);
        foreach (var pair in new[] { (sale, saleVersion), (rent, rentVersion) })
        {
            var next = MarketplaceFixtures.Pending(pair.Item1, MarketplaceFixtures.DecidedAt.AddMinutes(1), pair.Item2);
            Assert.Throws<InvalidOperationException>(() => next.ApproveAndPublish(Guid.NewGuid(), pair.Item1, pair.Item2,
                MarketplaceFixtures.Reviewer(next), MarketplaceFixtures.DecidedAt.AddMinutes(2), true,
                MarketplaceFixtures.Assessments(next, MarketplaceFixtures.DecidedAt.AddMinutes(1)), true, null));
            Assert.Null(next.ReviewDecision);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectionRequiresReasonWithoutMutation(string? reason)
    {
        var listing = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, publish: false);
        var next = MarketplaceFixtures.Pending(listing, MarketplaceFixtures.PublishedAt);
        Assert.ThrowsAny<ArgumentException>(() => next.Reject(Guid.NewGuid(), MarketplaceFixtures.Reviewer(next), reason!, MarketplaceFixtures.DecidedAt));
        Assert.Null(next.ReviewDecision);
        Assert.Equal(ListingSubmissionStatus.PendingReview, next.Status);
        Assert.Null(listing.PublishedSubmissionId);
    }

    private static void Approve(ListingSubmission next, PropertyListing listing, ListingSubmission previous) =>
        next.ApproveAndPublish(Guid.NewGuid(), listing, previous, MarketplaceFixtures.Reviewer(next),
            MarketplaceFixtures.DecidedAt, true, MarketplaceFixtures.Assessments(next, MarketplaceFixtures.RequestedAt), true, null);
}
