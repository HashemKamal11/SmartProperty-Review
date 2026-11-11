using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.ListingSubmissions.Review.Decide;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Create;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.CreateBoth;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Submit;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Withdraw;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

/// <summary>
/// The owner and administrator workflow end to end through the real handlers and real PostgreSQL: draft, submit,
/// approve or reject, withdraw and advertise again. Grants are seeded explicitly (nothing issues them); the evidence
/// provider, the reviewer-independence provider and the decide authorizer are test fakes, since production registers
/// ones that never answer. This proves the handlers and the database together; it does not prove the full workflow in
/// production, where evidence verification and independence checks have no real provider yet.
/// </summary>
public sealed class ListingWorkflowPersistenceTests(PostgreSqlFixture fixture) : MarketplaceWorkflowTestBase(fixture)
{
    // ---- advertising for sale, rent or both

    [Fact]
    public async Task BothNeedsASaleGrantAndARentGrantAndCreatesTwoSeparateListings()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale, ListingPurpose.Rent]);

        var result = await CreateBothAsync(world, sale: SaleContent(), rent: RentContent());

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.NotNull(result.Value.Sale);
        Assert.NotNull(result.Value.Rent);
        Assert.NotEqual(result.Value.Sale!.ListingId, result.Value.Rent!.ListingId);
        Assert.Equal(2, await CountAsync("marketplace.listings"));
        Assert.Equal(2, await CountAsync("marketplace.submissions"));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM marketplace.listings WHERE purpose = 'Sale'"));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM marketplace.listings WHERE purpose = 'Rent'"));
    }

    [Fact]
    public async Task BothWithoutTheRentGrantWritesNothingAtAll()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);

        var result = await CreateBothAsync(world, sale: SaleContent(), rent: RentContent());

        Assert.Equal("listing_submissions.not_eligible", result.Error!.Code);
        Assert.Equal(0, await CountAsync("marketplace.listings"));
        Assert.Equal(0, await CountAsync("marketplace.submissions"));
    }

    [Fact]
    public async Task OnlyTheRequestedSideIsCreated()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale, ListingPurpose.Rent]);

        var result = await CreateBothAsync(world, sale: SaleContent(), rent: null);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.NotNull(result.Value.Sale);
        Assert.Null(result.Value.Rent);
        Assert.Equal(1, await CountAsync("marketplace.listings"));
    }

    [Fact]
    public async Task AskingForNeitherSideIsAValidationFailure()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);

        var result = await CreateBothAsync(world, sale: null, rent: null);

        Assert.Equal("validation.failed", result.Error!.Code);
    }

    [Fact]
    public async Task BothIsRefusedAsAWholeWhenOneSideAlreadyHasAnOpenDraft()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale, ListingPurpose.Rent]);
        Assert.True((await CreateBothAsync(world, sale: SaleContent(), rent: null)).IsSuccess);

        var again = await CreateBothAsync(world, sale: SaleContent(), rent: RentContent());

        Assert.Equal("listing_submissions.open_revision_exists", again.Error!.Code);
        Assert.Equal(1, await CountAsync("marketplace.listings"));
        Assert.Equal(1, await CountAsync("marketplace.submissions"));
    }

    // ---- a property made from the place tree has no region

    [Fact]
    public async Task APropertyWithNoRegionIsAdvertisedUnderItsCityBecausePlaceTreePropertiesHaveNone()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        await ScalarObjectAsync($"UPDATE registry.properties SET address_region = NULL WHERE id = '{world.PropertyId}' RETURNING 1");

        var result = await CreateBothAsync(world, sale: SaleContent(), rent: null);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal("Cairo", await TextAsync($"SELECT content_snapshot->'Payload'->>'Region' FROM marketplace.submissions WHERE id = '{result.Value.Sale!.SubmissionId}'"));
    }

    [Fact]
    public async Task AnExplicitRegionAlwaysWinsOverTheCity()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);

        var result = await CreateBothAsync(world, sale: SaleContent(), rent: null);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal("Cairo Governorate", await TextAsync($"SELECT content_snapshot->'Payload'->>'Region' FROM marketplace.submissions WHERE id = '{result.Value.Sale!.SubmissionId}'"));
    }

    [Fact]
    public async Task TheCityStandingInAsTheRegionNeverChangesThePropertyRecord()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        await ScalarObjectAsync($"UPDATE registry.properties SET address_region = NULL WHERE id = '{world.PropertyId}' RETURNING 1");
        var before = await TextAsync($"SELECT concat_ws('|', address_country_code, coalesce(address_region, '<null>'), address_city, address_district, updated_at::text) FROM registry.properties WHERE id = '{world.PropertyId}'");

        var draft = await SubmittedAsync(world, ListingPurpose.Sale);
        var approved = await ApproveAsync(world, draft.SubmissionId);

        Assert.True(approved.IsSuccess, approved.Error?.Code);
        Assert.Equal(before, await TextAsync($"SELECT concat_ws('|', address_country_code, coalesce(address_region, '<null>'), address_city, address_district, updated_at::text) FROM registry.properties WHERE id = '{world.PropertyId}'"));
        Assert.Equal("<null>", await TextAsync($"SELECT coalesce(address_region, '<null>') FROM registry.properties WHERE id = '{world.PropertyId}'"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankRegionCountsAsMissingAndOnlyTheCityIsEverSubstituted(string blank)
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        await ScalarObjectAsync($"UPDATE registry.properties SET address_region = '{blank}' WHERE id = '{world.PropertyId}' RETURNING 1");

        var result = await CreateBothAsync(world, sale: SaleContent(), rent: null);

        Assert.True(result.IsSuccess, result.Error?.Code);
        var submission = result.Value.Sale!.SubmissionId;
        Assert.Equal("Cairo", await TextAsync($"SELECT content_snapshot->'Payload'->>'Region' FROM marketplace.submissions WHERE id = '{submission}'"));
        // The district is the recorded district, not a substitute for anything; the stored property keeps its blank value.
        Assert.Equal(await TextAsync($"SELECT address_district FROM registry.properties WHERE id = '{world.PropertyId}'"), await TextAsync($"SELECT content_snapshot->'Payload'->>'District' FROM marketplace.submissions WHERE id = '{submission}'"));
        Assert.Equal(blank, await TextAsync($"SELECT address_region FROM registry.properties WHERE id = '{world.PropertyId}'"));
    }

    [Fact]
    public async Task RecordingARealRegionAfterTheDraftMakesTheDraftStaleInsteadOfPublishingTheCityLabel()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        await ScalarObjectAsync($"UPDATE registry.properties SET address_region = NULL WHERE id = '{world.PropertyId}' RETURNING 1");
        var draft = await SubmittedAsync(world, ListingPurpose.Sale);
        await ScalarObjectAsync($"UPDATE registry.properties SET address_region = 'Giza Governorate' WHERE id = '{world.PropertyId}' RETURNING 1");

        var approved = await ApproveAsync(world, draft.SubmissionId);

        Assert.True(approved.IsFailure);
        Assert.Equal("listing_submissions.cannot_publish", approved.Error!.Code);
        Assert.NotEqual("Published", await TextAsync($"SELECT publication_status FROM marketplace.listings WHERE id = '{draft.ListingId}'"));
        Assert.Equal("Giza Governorate", await TextAsync($"SELECT address_region FROM registry.properties WHERE id = '{world.PropertyId}'"));
    }

    [Fact]
    public async Task APropertyWithNeitherARegionNorACityStillCannotBeAdvertised()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        await ScalarObjectAsync($"UPDATE registry.properties SET address_region = NULL, address_city = NULL WHERE id = '{world.PropertyId}' RETURNING 1");

        var result = await CreateBothAsync(world, sale: SaleContent(), rent: null);

        Assert.Equal("listing_submissions.property_unavailable", result.Error!.Code);
        Assert.Equal(0, await CountAsync("marketplace.submissions"));
    }

    // ---- publishing

    [Fact]
    public async Task ApprovalPublishesOnlyAfterEveryCheckAndRecordsTheDecision()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var draft = await SubmittedAsync(world, ListingPurpose.Sale);

        var approved = await ApproveAsync(world, draft.SubmissionId);

        Assert.True(approved.IsSuccess, approved.Error?.Code);
        Assert.Equal(ListingSubmissionStatus.Approved, approved.Value.Status);
        Assert.Equal("Approved", await TextAsync($"SELECT status FROM marketplace.submissions WHERE id = '{draft.SubmissionId}'"));
        Assert.Equal("Published", await TextAsync($"SELECT publication_status FROM marketplace.listings WHERE id = '{draft.ListingId}'"));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM marketplace.review_decisions WHERE submission_id = '{draft.SubmissionId}' AND approved"));
    }

    [Fact]
    public async Task ADecidedSubmissionCannotBeDecidedAgain()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var draft = await SubmittedAsync(world, ListingPurpose.Sale);
        Assert.True((await ApproveAsync(world, draft.SubmissionId)).IsSuccess);

        var again = await ApproveAsync(world, draft.SubmissionId);
        var reject = await RejectAsync(world, draft.SubmissionId, "late");

        Assert.Equal("listing_submissions.not_pending_review", again.Error!.Code);
        Assert.Equal("listing_submissions.not_pending_review", reject.Error!.Code);
    }

    [Fact]
    public async Task ApprovalIsRefusedClearlyWhileNoEvidenceProviderCanAnswerAndNothingChanges()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var draft = await SubmittedAsync(world, ListingPurpose.Sale);

        var result = await ApproveAsync(world, draft.SubmissionId, evidence: new FakeEvidence { Available = false });

        Assert.Equal("listing_submissions.evidence_verification_unavailable", result.Error!.Code);
        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
        Assert.Equal("PendingReview", await TextAsync($"SELECT status FROM marketplace.submissions WHERE id = '{draft.SubmissionId}'"));
        Assert.Equal("Unpublished", await TextAsync($"SELECT publication_status FROM marketplace.listings WHERE id = '{draft.ListingId}'"));
    }

    [Fact]
    public async Task ADecisionIsRefusedWhenReviewerIndependenceCannotBeEstablished()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var draft = await SubmittedAsync(world, ListingPurpose.Sale);
        var unknown = new FakeIndependence(new ReviewerIndependenceFacts(null, null, null));

        var approve = await ApproveAsync(world, draft.SubmissionId, independence: unknown);
        var reject = await RejectAsync(world, draft.SubmissionId, "no", independence: unknown);

        Assert.Equal("listing_submissions.independence_unverifiable", approve.Error!.Code);
        Assert.Equal("listing_submissions.independence_unverifiable", reject.Error!.Code);
        Assert.Equal("PendingReview", await TextAsync($"SELECT status FROM marketplace.submissions WHERE id = '{draft.SubmissionId}'"));
    }

    [Fact]
    public async Task TheSubmitterCanNeverReviewTheirOwnListing()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var draft = await SubmittedAsync(world, ListingPurpose.Sale);

        var result = await ApproveAsync(world, draft.SubmissionId, reviewer: world.Owner);

        Assert.Equal("listing_submissions.reviewer_not_independent", result.Error!.Code);
        Assert.Equal("PendingReview", await TextAsync($"SELECT status FROM marketplace.submissions WHERE id = '{draft.SubmissionId}'"));
    }

    [Fact]
    public async Task AReviewerWithoutTheDecidePermissionIsForbidden()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var draft = await SubmittedAsync(world, ListingPurpose.Sale);

        var result = await ApproveAsync(world, draft.SubmissionId, allowed: false);

        Assert.Equal("authorization.forbidden", result.Error!.Code);
    }

    [Fact]
    public async Task ARevokedGrantStopsPublicationEvenAfterSubmission()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var draft = await SubmittedAsync(world, ListingPurpose.Sale);
        await RevokeAllGrantsAsync(world);

        var result = await ApproveAsync(world, draft.SubmissionId);

        Assert.Equal("listing_submissions.cannot_publish", result.Error!.Code);
        Assert.Equal("Unpublished", await TextAsync($"SELECT publication_status FROM marketplace.listings WHERE id = '{draft.ListingId}'"));
    }

    [Fact]
    public async Task ARejectionKeepsTheReasonAndPublishesNothing()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var draft = await SubmittedAsync(world, ListingPurpose.Sale);

        var result = await RejectAsync(world, draft.SubmissionId, "Photos do not match the property.");

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal("Rejected", await TextAsync($"SELECT status FROM marketplace.submissions WHERE id = '{draft.SubmissionId}'"));
        Assert.Equal("Unpublished", await TextAsync($"SELECT publication_status FROM marketplace.listings WHERE id = '{draft.ListingId}'"));
        Assert.Equal(
            "Photos do not match the property.",
            await TextAsync($"SELECT reason FROM marketplace.review_decisions WHERE submission_id = '{draft.SubmissionId}'"));
    }

    [Fact]
    public async Task ARejectionNeedsAReason()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var draft = await SubmittedAsync(world, ListingPurpose.Sale);

        var result = await RejectAsync(world, draft.SubmissionId, "   ");

        Assert.Equal("validation.failed", result.Error!.Code);
    }

    [Fact]
    public async Task TwoReviewersApprovingTogetherPublishExactlyOnce()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var draft = await SubmittedAsync(world, ListingPurpose.Sale);

        var results = await Task.WhenAll(
            ApproveAsync(world, draft.SubmissionId),
            ApproveAsync(world, draft.SubmissionId),
            ApproveAsync(world, draft.SubmissionId));

        Assert.Single(results, result => result.IsSuccess);
        Assert.All(results.Where(result => result.IsFailure), result => Assert.Equal("listing_submissions.not_pending_review", result.Error!.Code));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM marketplace.review_decisions WHERE submission_id = '{draft.SubmissionId}'"));
    }

    // ---- withdrawing and advertising again

    [Fact]
    public async Task OnlyTheAuthorCanWithdrawAndOnlyAPublishedListing()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var draft = await SubmittedAsync(world, ListingPurpose.Sale);

        var beforePublishing = await WithdrawAsync(world, draft.ListingId);
        Assert.True((await ApproveAsync(world, draft.SubmissionId)).IsSuccess);
        var stranger = await WithdrawAsync(world, draft.ListingId, actor: world.Stranger);
        var owner = await WithdrawAsync(world, draft.ListingId);
        var twice = await WithdrawAsync(world, draft.ListingId);

        Assert.Equal("listing_submissions.not_found", beforePublishing.Error!.Code);
        Assert.Equal("listing_submissions.not_found", stranger.Error!.Code);
        Assert.True(owner.IsSuccess, owner.Error?.Code);
        Assert.Equal("listing_submissions.listing_not_published", twice.Error!.Code);
        Assert.Equal("Withdrawn", await TextAsync($"SELECT publication_status FROM marketplace.listings WHERE id = '{draft.ListingId}'"));
        Assert.Equal("Closed", await TextAsync($"SELECT availability_status FROM marketplace.listings WHERE id = '{draft.ListingId}'"));
    }

    [Fact]
    public async Task AWithdrawalSucceedsEvenWhenTheGrantHasSinceBeenRevoked()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var draft = await SubmittedAsync(world, ListingPurpose.Sale);
        Assert.True((await ApproveAsync(world, draft.SubmissionId)).IsSuccess);
        await RevokeAllGrantsAsync(world);

        var result = await WithdrawAsync(world, draft.ListingId);

        Assert.True(result.IsSuccess, result.Error?.Code);
    }

    [Fact]
    public async Task AWithdrawnListingIsAdvertisedAgainOnlyByANewSubmittedAndApprovedVersion()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale]);
        var first = await SubmittedAsync(world, ListingPurpose.Sale);
        Assert.True((await ApproveAsync(world, first.SubmissionId)).IsSuccess);
        Assert.True((await WithdrawAsync(world, first.ListingId)).IsSuccess);

        var next = await SubmittedAsync(world, ListingPurpose.Sale);
        var republished = await ApproveAsync(world, next.SubmissionId);

        Assert.Equal(first.ListingId, next.ListingId);
        Assert.Equal(2, next.Version);
        Assert.True(republished.IsSuccess, republished.Error?.Code);
        Assert.Equal("Published", await TextAsync($"SELECT publication_status FROM marketplace.listings WHERE id = '{first.ListingId}'"));
        Assert.Equal("Open", await TextAsync($"SELECT availability_status FROM marketplace.listings WHERE id = '{first.ListingId}'"));
        Assert.Equal("Superseded", await TextAsync($"SELECT status FROM marketplace.submissions WHERE id = '{first.SubmissionId}'"));
        Assert.Equal(next.SubmissionId, await GuidAsync($"SELECT published_submission_id FROM marketplace.listings WHERE id = '{first.ListingId}'"));
    }

    [Fact]
    public async Task WithdrawingTheSaleListingLeavesTheRentListingPublished()
    {
        var world = await SeedAsync(grants: [ListingPurpose.Sale, ListingPurpose.Rent]);
        var sale = await SubmittedAsync(world, ListingPurpose.Sale);
        var rent = await SubmittedAsync(world, ListingPurpose.Rent);
        Assert.True((await ApproveAsync(world, sale.SubmissionId)).IsSuccess);
        Assert.True((await ApproveAsync(world, rent.SubmissionId)).IsSuccess);

        Assert.True((await WithdrawAsync(world, sale.ListingId)).IsSuccess);

        Assert.Equal("Withdrawn", await TextAsync($"SELECT publication_status FROM marketplace.listings WHERE id = '{sale.ListingId}'"));
        Assert.Equal("Published", await TextAsync($"SELECT publication_status FROM marketplace.listings WHERE id = '{rent.ListingId}'"));
    }

}
