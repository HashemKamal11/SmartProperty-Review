using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Application.Marketplace.Requests;
using SmartProperty.Application.Marketplace.Requests.Create;
using SmartProperty.Application.Marketplace.Requests.Decide;
using SmartProperty.Application.Marketplace.Requests.Read;
using SmartProperty.Application.Marketplace.Requests.Settings;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

/// <summary>
/// Purchase and rental requests, dated commitments, overlap and sale rules, lazy signing expiry and the
/// administrator's signing window, through the real handlers and real PostgreSQL. Grants, the evidence provider and
/// the reviewer-independence provider are test fakes. This proves the handlers and the database together; it does not
/// prove the workflow in production, where grant issuance, evidence verification and reviewer independence have no
/// real provider yet.
/// </summary>
public sealed class MarketplaceRequestPersistenceTests(PostgreSqlFixture fixture) : MarketplaceWorkflowTestBase(fixture)
{
    private static readonly DateOnly June1 = new(2027, 6, 1);

    // ---- making a rental request

    [Fact]
    public async Task ARentalRequestCopiesThePriceAndTermsFromTheApprovedListing()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var customer = await NewUserAsync();

        var result = await RentalRequestAsync(customer, rent.ListingId, RentPeriod.Monthly, June1, June1.AddDays(60), "Hello");

        Assert.True(result.IsSuccess, result.Error?.Code);
        var id = result.Value.RequestId;
        Assert.Equal(20_000m, await DecimalAsync($"SELECT amount FROM marketplace.rental_requests WHERE id = '{id}'"));
        Assert.Equal("Monthly", await TextAsync($"SELECT pricing_period FROM marketplace.rental_requests WHERE id = '{id}'"));
        Assert.Equal("EGP", (await TextAsync($"SELECT currency FROM marketplace.rental_requests WHERE id = '{id}'")).Trim());
        Assert.Equal("14:00:00", await TextAsync($"SELECT check_in_time::text FROM marketplace.rental_requests WHERE id = '{id}'"));
        Assert.Equal("Africa/Cairo", await TextAsync($"SELECT time_zone_id FROM marketplace.rental_requests WHERE id = '{id}'"));
        Assert.Equal(rent.SubmissionId, await GuidAsync($"SELECT submission_id FROM marketplace.rental_requests WHERE id = '{id}'"));
        Assert.Equal("Submitted", result.Value.Status);
    }

    [Theory]
    [InlineData("2027-05-20", "2027-06-05")] // starts before the window
    [InlineData("2027-08-20", "2027-09-05")] // ends after the window
    [InlineData("2027-06-05", "2027-06-05")] // no nights
    [InlineData("2027-06-10", "2027-06-02")] // ends before it starts
    public async Task AStayOutsideTheAvailabilityWindowOrWithoutNightsIsRefused(string start, string end)
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var customer = await NewUserAsync();

        var result = await RentalRequestAsync(
            customer, rent.ListingId, RentPeriod.Daily, DateOnly.Parse(start), DateOnly.Parse(end), null);

        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal(0, await CountAsync("marketplace.rental_requests"));
    }

    [Fact]
    public async Task APricingPeriodTheListingDoesNotOfferIsRefused()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);

        var result = await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Yearly, June1, June1.AddDays(30), null);

        Assert.Equal("marketplace_requests.pricing_option_unavailable", result.Error!.Code);
    }

    [Fact]
    public async Task AStayCannotStartInThePastInThePropertysOwnCalendar()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        // The window opens 2027-06-01; with the clock set after it, an earlier start day is in the past locally.
        Clock.Fixed = new DateTimeOffset(2027, 6, 10, 21, 30, 0, TimeSpan.Zero);

        var past = await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, new DateOnly(2027, 6, 9), new DateOnly(2027, 6, 12), null);
        // 21:30 UTC is already the 11th in Cairo (UTC+3 in summer), so the 10th is in the past there but the 11th is not.
        var localToday = await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, new DateOnly(2027, 6, 11), new DateOnly(2027, 6, 14), null);
        var yesterdayLocally = await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, new DateOnly(2027, 6, 10), new DateOnly(2027, 6, 14), null);

        Assert.Equal("validation.failed", past.Error!.Code);
        Assert.True(localToday.IsSuccess, localToday.Error?.Code);
        Assert.Equal("validation.failed", yesterdayLocally.Error!.Code);
    }

    [Fact]
    public async Task ACustomerCannotRequestTheirOwnListing()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);

        var result = await RentalRequestAsync(world.Owner, rent.ListingId, RentPeriod.Daily, June1, June1.AddDays(3), null);

        Assert.Equal("marketplace_requests.own_listing", result.Error!.Code);
    }

    [Fact]
    public async Task AListingThatIsNotPublishedOrNotThePurposeIsNotFound()
    {
        var world = await SeedAsync([ListingPurpose.Rent, ListingPurpose.Sale]);
        var submittedOnly = await SubmittedAsync(world, ListingPurpose.Rent);
        var sale = await PublishAsync(world, ListingPurpose.Sale);
        var customer = await NewUserAsync();

        var unpublished = await RentalRequestAsync(customer, submittedOnly.ListingId, RentPeriod.Daily, June1, June1.AddDays(3), null);
        var wrongPurpose = await RentalRequestAsync(customer, sale.ListingId, RentPeriod.Daily, June1, June1.AddDays(3), null);
        var unknown = await RentalRequestAsync(customer, Guid.NewGuid(), RentPeriod.Daily, June1, June1.AddDays(3), null);

        Assert.Equal("marketplace_requests.listing_unavailable", unpublished.Error!.Code);
        Assert.Equal("marketplace_requests.listing_unavailable", wrongPurpose.Error!.Code);
        Assert.Equal("marketplace_requests.listing_unavailable", unknown.Error!.Code);
    }

    [Fact]
    public async Task AListingWhoseAdvertiserLostTheGrantStopsTakingRequests()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        await RevokeAllGrantsAsync(world);

        var result = await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, June1, June1.AddDays(3), null);

        Assert.Equal("marketplace_requests.listing_unavailable", result.Error!.Code);
    }

    [Fact]
    public async Task ACustomerHasOneOpenRequestPerListingAndMayRequestAgainAfterWithdrawing()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var customer = await NewUserAsync();
        var first = await RentalRequestAsync(customer, rent.ListingId, RentPeriod.Daily, June1, June1.AddDays(3), null);

        var duplicate = await RentalRequestAsync(customer, rent.ListingId, RentPeriod.Daily, June1.AddDays(10), June1.AddDays(13), null);
        var withdrawn = await WithdrawRequestAsync(customer, RequestKind.Rental, first.Value.RequestId);
        var again = await RentalRequestAsync(customer, rent.ListingId, RentPeriod.Daily, June1.AddDays(10), June1.AddDays(13), null);

        Assert.Equal("marketplace_requests.already_requested", duplicate.Error!.Code);
        Assert.True(withdrawn.IsSuccess, withdrawn.Error?.Code);
        Assert.True(again.IsSuccess, again.Error?.Code);
    }

    [Fact]
    public async Task AnotherCustomerCanRequestOverlappingDatesWhileNothingIsCommitted()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);

        var first = await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, June1, June1.AddDays(5), null);
        var second = await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, June1.AddDays(2), June1.AddDays(7), null);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess, second.Error?.Code);
    }

    // ---- withdrawing and rejecting

    [Fact]
    public async Task OnlyTheCustomerCanWithdrawAndOnlyWhileOpen()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var customer = await NewUserAsync();
        var request = (await RentalRequestAsync(customer, rent.ListingId, RentPeriod.Daily, June1, June1.AddDays(3), null)).Value;

        var stranger = await WithdrawRequestAsync(world.Stranger, RequestKind.Rental, request.RequestId);
        var owner = await WithdrawRequestAsync(world.Owner, RequestKind.Rental, request.RequestId);
        var mine = await WithdrawRequestAsync(customer, RequestKind.Rental, request.RequestId);
        var twice = await WithdrawRequestAsync(customer, RequestKind.Rental, request.RequestId);

        Assert.Equal("marketplace_requests.not_found", stranger.Error!.Code);
        Assert.Equal("marketplace_requests.not_found", owner.Error!.Code);
        Assert.True(mine.IsSuccess, mine.Error?.Code);
        Assert.Equal("marketplace_requests.not_open", twice.Error!.Code);
        Assert.Equal("WithdrawnByRequester", await TextAsync($"SELECT status FROM marketplace.rental_requests WHERE id = '{request.RequestId}'"));
    }

    [Fact]
    public async Task OnlyTheAdvertiserCanRejectARequestOnTheirListing()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var request = (await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, June1, June1.AddDays(3), null)).Value;

        var stranger = await RejectRequestAsync(world.Stranger, RequestKind.Rental, request.RequestId);
        var owner = await RejectRequestAsync(world.Owner, RequestKind.Rental, request.RequestId);
        var again = await RejectRequestAsync(world.Owner, RequestKind.Rental, request.RequestId);

        Assert.Equal("marketplace_requests.not_found", stranger.Error!.Code);
        Assert.True(owner.IsSuccess, owner.Error?.Code);
        Assert.Equal("marketplace_requests.not_open", again.Error!.Code);
        Assert.Equal("Rejected", await TextAsync($"SELECT status FROM marketplace.rental_requests WHERE id = '{request.RequestId}'"));
    }

    // ---- accepting

    [Fact]
    public async Task AcceptingARentalCommitsItsDatesClosesBothAdvertisementsAndClosesTheOtherRequestsKeepingHistory()
    {
        var world = await SeedAsync([ListingPurpose.Rent, ListingPurpose.Sale]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var sale = await PublishAsync(world, ListingPurpose.Sale);
        var winner = (await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Monthly, June1, June1.AddDays(60), null)).Value;
        var loser = (await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, June1.AddDays(10), June1.AddDays(15), null)).Value;
        var buyer = (await PurchaseRequestAsync(await NewUserAsync(), sale.ListingId, 4_900_000m, null)).Value;

        var accepted = await AcceptAsync(world.Owner, RequestKind.Rental, winner.RequestId);

        Assert.True(accepted.IsSuccess, accepted.Error?.Code);
        var commitment = accepted.Value.CommitmentId;
        Assert.Equal("PendingSignature", await TextAsync($"SELECT status FROM marketplace.property_commitments WHERE id = '{commitment}'"));
        Assert.Equal("Accepted", await TextAsync($"SELECT status FROM marketplace.rental_requests WHERE id = '{winner.RequestId}'"));
        Assert.Equal("ClosedListingUnavailable", await TextAsync($"SELECT status FROM marketplace.rental_requests WHERE id = '{loser.RequestId}'"));
        Assert.Equal("ClosedListingUnavailable", await TextAsync($"SELECT status FROM marketplace.purchase_requests WHERE id = '{buyer.RequestId}'"));
        Assert.Equal("Withdrawn", await TextAsync($"SELECT publication_status FROM marketplace.listings WHERE id = '{rent.ListingId}'"));
        Assert.Equal("Withdrawn", await TextAsync($"SELECT publication_status FROM marketplace.listings WHERE id = '{sale.ListingId}'"));
        Assert.Equal(June1.ToString("yyyy-MM-dd"), await TextAsync($"SELECT start_date::text FROM marketplace.property_commitments WHERE id = '{commitment}'"));
        Assert.Equal(3, await CountAsync("marketplace.rental_requests") + await CountAsync("marketplace.purchase_requests") - 0);
    }

    [Fact]
    public async Task OnlyTheAdvertiserWithAValidGrantCanAccept()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var request = (await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, June1, June1.AddDays(3), null)).Value;

        var stranger = await AcceptAsync(world.Stranger, RequestKind.Rental, request.RequestId);
        await RevokeAllGrantsAsync(world);
        var revoked = await AcceptAsync(world.Owner, RequestKind.Rental, request.RequestId);

        Assert.Equal("marketplace_requests.not_found", stranger.Error!.Code);
        Assert.Equal("listing_submissions.not_eligible", revoked.Error!.Code);
        Assert.Equal(0, await CountAsync("marketplace.property_commitments"));
        Assert.Equal("Submitted", await TextAsync($"SELECT status FROM marketplace.rental_requests WHERE id = '{request.RequestId}'"));
    }

    [Fact]
    public async Task AnAcceptedRequestCannotBeAcceptedTwice()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var request = (await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, June1, June1.AddDays(3), null)).Value;
        Assert.True((await AcceptAsync(world.Owner, RequestKind.Rental, request.RequestId)).IsSuccess);

        var again = await AcceptAsync(world.Owner, RequestKind.Rental, request.RequestId);

        Assert.Equal("marketplace_requests.not_open", again.Error!.Code);
        Assert.Equal(1, await CountAsync("marketplace.property_commitments"));
    }

    [Fact]
    public async Task TwoOwnersAcceptingOverlappingRequestsAtTheSameTimeCommitExactlyOne()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var first = (await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, June1, June1.AddDays(6), null)).Value;
        var second = (await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, June1.AddDays(3), June1.AddDays(9), null)).Value;

        var results = await Task.WhenAll(
            AcceptAsync(world.Owner, RequestKind.Rental, first.RequestId),
            AcceptAsync(world.Owner, RequestKind.Rental, second.RequestId),
            AcceptAsync(world.Owner, RequestKind.Rental, first.RequestId));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(1, await CountAsync("marketplace.property_commitments"));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM marketplace.rental_requests WHERE status = 'Accepted'"));
    }

    [Fact]
    public async Task TheDatabaseRefusesOverlappingRentalsEvenIfAHandlerGotItWrong()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var first = (await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, June1, June1.AddDays(6), null)).Value;
        var second = (await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, June1.AddDays(3), June1.AddDays(9), null)).Value;

        await using var scope = Scope();
        var repository = scope.Get<IMarketplaceRequestWriteRepository>();
        await repository.LockPropertyAsync(world.PropertyId);
        await repository.GetGrantForSharedLockAsync(world.Owner, world.PropertyId, ListingPurpose.Sale);
        await repository.GetGrantForSharedLockAsync(world.Owner, world.PropertyId, ListingPurpose.Rent);
        var listing = await repository.GetListingAsync(world.PropertyId, ListingPurpose.Rent);
        var requests = await repository.GetOpenRentalRequestsAsync(world.PropertyId);
        var a = requests.Single(r => r.Id == first.RequestId);
        var b = requests.Single(r => r.Id == second.RequestId);
        var now = DateTimeOffset.UtcNow;

        // A handler that skipped its own overlap check: both are accepted in one unit of work.
        await repository.AddCommitmentAsync(CommitmentAcceptance.AcceptRental(listing!, null, a, [], [], world.Owner, now, now.AddHours(72)));
        await repository.AddCommitmentAsync(CommitmentAcceptance.AcceptRental(listing!, null, b, [], [], world.Owner, now.AddTicks(1), now.AddHours(72)));

        var exception = await Assert.ThrowsAsync<ListingWriteConflictException>(() => repository.SaveChangesAsync());

        Assert.Equal(ListingWriteConflictKind.DatesUnavailable, exception.Kind);
        Assert.Equal(0, await CountAsync("marketplace.property_commitments"));
        Assert.Equal("Submitted", await TextAsync($"SELECT status FROM marketplace.rental_requests WHERE id = '{first.RequestId}'"));
    }

    [Fact]
    public async Task BackToBackRentalsAreBothAcceptedBecauseTheCheckOutDayIsFree()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var first = await RentAndAcceptAsync(world, June1, June1.AddDays(10));

        // The advertisement closed with the first acceptance; advertising again is an explicit new submission.
        var again = await PublishAsync(world, ListingPurpose.Rent);
        var second = await RentalRequestAsync(await NewUserAsync(), again.ListingId, RentPeriod.Daily, June1.AddDays(10), June1.AddDays(14), null);
        var overlapping = await RentalRequestAsync(await NewUserAsync(), again.ListingId, RentPeriod.Daily, June1.AddDays(9), June1.AddDays(12), null);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess, second.Error?.Code);
        Assert.Equal("marketplace_requests.dates_unavailable", overlapping.Error!.Code);
        Assert.True((await AcceptAsync(world.Owner, RequestKind.Rental, second.Value.RequestId)).IsSuccess);
        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM marketplace.property_commitments WHERE status = 'PendingSignature'"));
    }

    [Fact]
    public async Task AnAcceptanceNeverCancelsAnExistingCommitmentOrChangesItsDates()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var first = await RentAndAcceptAsync(world, June1, June1.AddDays(10));
        var firstCommitment = first.Value.CommitmentId;
        await ConfirmAsync(world.Owner, firstCommitment, CommitmentStep.Signed);

        var again = await PublishAsync(world, ListingPurpose.Rent);
        Assert.True((await RentalRequestAsync(await NewUserAsync(), again.ListingId, RentPeriod.Daily, June1.AddDays(20), June1.AddDays(22), null)).IsSuccess);
        Assert.True((await AcceptAsync(world.Owner, RequestKind.Rental, await LatestRequestAsync("rental_requests"))).IsSuccess);

        Assert.Equal("Signed", await TextAsync($"SELECT status FROM marketplace.property_commitments WHERE id = '{firstCommitment}'"));
        Assert.Equal(June1.ToString("yyyy-MM-dd"), await TextAsync($"SELECT start_date::text FROM marketplace.property_commitments WHERE id = '{firstCommitment}'"));
    }

    // ---- sale restrictions

    [Fact]
    public async Task ADailyRentalInForceBlocksPublishingAndAcceptingASale()
    {
        var world = await SeedAsync([ListingPurpose.Rent, ListingPurpose.Sale]);
        var sale = await PublishAsync(world, ListingPurpose.Sale);
        var buyer = (await PurchaseRequestAsync(await NewUserAsync(), sale.ListingId, 4_000_000m, null)).Value;
        // A daily rental is committed while the sale request is pending (e.g. accepted just before): the sale is blocked.
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var daily = (await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, June1, June1.AddDays(4), null)).Value;
        var accepted = await AcceptAsync(world.Owner, RequestKind.Rental, daily.RequestId);
        Assert.True(accepted.IsSuccess, accepted.Error?.Code);

        // Accepting the sale closed with the rental's acceptance; advertise it again and the review refuses.
        var resubmitted = await SubmittedAsync(world, ListingPurpose.Sale);
        var approval = await ApproveAsync(world, resubmitted.SubmissionId);

        Assert.Equal("listing_submissions.short_stay_occupancy", approval.Error!.Code);
        Assert.Equal("ClosedListingUnavailable", await TextAsync($"SELECT status FROM marketplace.purchase_requests WHERE id = '{buyer.RequestId}'"));
        Assert.Equal("PendingReview", await TextAsync($"SELECT status FROM marketplace.submissions WHERE id = '{resubmitted.SubmissionId}'"));
    }

    [Fact]
    public async Task AMonthlyRentalInForceDoesNotBlockASaleAndIsPreservedBySelling()
    {
        var world = await SeedAsync([ListingPurpose.Rent, ListingPurpose.Sale]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var monthly = (await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Monthly, June1, June1.AddDays(90), null)).Value;
        var rentalCommitment = (await AcceptAsync(world.Owner, RequestKind.Rental, monthly.RequestId)).Value.CommitmentId;
        await ConfirmAsync(world.Owner, rentalCommitment, CommitmentStep.Signed);

        var sale = await PublishAsync(world, ListingPurpose.Sale);
        var buyer = (await PurchaseRequestAsync(await NewUserAsync(), sale.ListingId, 4_000_000m, "Offer")).Value;
        var sold = await AcceptAsync(world.Owner, RequestKind.Purchase, buyer.RequestId);

        Assert.True(sold.IsSuccess, sold.Error?.Code);
        Assert.Equal("Signed", await TextAsync($"SELECT status FROM marketplace.property_commitments WHERE id = '{rentalCommitment}'"));
        Assert.Equal("Sale", await TextAsync($"SELECT kind FROM marketplace.property_commitments WHERE id = '{sold.Value.CommitmentId}'"));
    }

    [Fact]
    public async Task ASaleCommitmentBlocksFurtherSalesRentalsAndRequests()
    {
        var world = await SeedAsync([ListingPurpose.Rent, ListingPurpose.Sale]);
        var sale = await PublishAsync(world, ListingPurpose.Sale);
        var buyer = (await PurchaseRequestAsync(await NewUserAsync(), sale.ListingId, 4_000_000m, null)).Value;
        Assert.True((await AcceptAsync(world.Owner, RequestKind.Purchase, buyer.RequestId)).IsSuccess);

        var rentDraft = await SubmittedAsync(world, ListingPurpose.Rent);
        var rentApproval = await ApproveAsync(world, rentDraft.SubmissionId);
        var saleDraft = await SubmittedAsync(world, ListingPurpose.Sale);
        var saleApproval = await ApproveAsync(world, saleDraft.SubmissionId);

        Assert.Equal("listing_submissions.property_committed", rentApproval.Error!.Code);
        Assert.Equal("listing_submissions.property_committed", saleApproval.Error!.Code);
    }

    // ---- the life of a commitment, and lazy expiry

    [Fact]
    public async Task TheOwnerConfirmsSignedThenHandoverThenReturnAndEachStepIsOrdered()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var commitment = (await RentAndAcceptAsync(world, June1, June1.AddDays(10))).Value.CommitmentId;

        var early = await ConfirmAsync(world.Owner, commitment, CommitmentStep.Handover);
        var stranger = await ConfirmAsync(world.Stranger, commitment, CommitmentStep.Signed);
        var signed = await ConfirmAsync(world.Owner, commitment, CommitmentStep.Signed);
        var handover = await ConfirmAsync(world.Owner, commitment, CommitmentStep.Handover);
        var returned = await ConfirmAsync(world.Owner, commitment, CommitmentStep.Return);

        Assert.Equal("marketplace_commitments.step_not_allowed", early.Error!.Code);
        Assert.Equal("marketplace_commitments.not_found", stranger.Error!.Code);
        Assert.Equal("Signed", signed.Value.Status);
        Assert.Equal("Occupied", handover.Value.Status);
        Assert.Equal("Completed", returned.Value.Status);
    }

    [Fact]
    public async Task AnOccupiedShortStayStillBlocksASaleAfterItsEndDateUntilTheReturnIsConfirmed()
    {
        var world = await SeedAsync([ListingPurpose.Rent, ListingPurpose.Sale]);
        var commitment = (await RentAndAcceptAsync(world, June1, June1.AddDays(5))).Value.CommitmentId;
        await ConfirmAsync(world.Owner, commitment, CommitmentStep.Signed);
        await ConfirmAsync(world.Owner, commitment, CommitmentStep.Handover);
        // Weeks after the stay was due to end nothing has confirmed the tenant left: the calendar does not decide.
        Clock.Fixed = new DateTimeOffset(2027, 6, 20, 12, 0, 0, TimeSpan.Zero);
        var saleDraft = await SubmittedAsync(world, ListingPurpose.Sale);

        var whileOccupied = await ApproveAsync(world, saleDraft.SubmissionId);
        var returned = await ConfirmAsync(world.Owner, commitment, CommitmentStep.Return);
        var afterReturn = await ApproveAsync(world, saleDraft.SubmissionId);

        Assert.Equal("listing_submissions.short_stay_occupancy", whileOccupied.Error!.Code);
        Assert.True(returned.IsSuccess, returned.Error?.Code);
        Assert.True(afterReturn.IsSuccess, afterReturn.Error?.Code);
    }


    [Fact]
    public async Task AnUnsignedCommitmentPastItsDeadlineIsReleasedWithoutAnyBackgroundJob()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        Assert.True((await SetWindowAsync(world.Reviewer, 1, allowed: true)).IsSuccess);
        var first = await RentAndAcceptAsync(world, June1, June1.AddDays(10));
        var firstCommitment = first.Value.CommitmentId;

        // Two hours later the unsigned commitment is overdue. Nothing has run: it is still stored as pending.
        Clock.Fixed = DateTimeOffset.UtcNow.AddHours(2);
        Assert.Equal("PendingSignature", await TextAsync($"SELECT status FROM marketplace.property_commitments WHERE id = '{firstCommitment}'"));
        var shown = await CommitmentsAsync(world.Owner, CommitmentRole.Owner);
        var signLate = await ConfirmAsync(world.Owner, firstCommitment, CommitmentStep.Signed);
        var again = await PublishAsync(world, ListingPurpose.Rent);
        var second = await RentalRequestAsync(await NewUserAsync(), again.ListingId, RentPeriod.Daily, June1.AddDays(2), June1.AddDays(6), null);
        var accepted = await AcceptAsync(world.Owner, RequestKind.Rental, second.Value.RequestId);

        Assert.Equal("Expired", shown.Items.Single().Status);
        Assert.Equal("marketplace_commitments.step_not_allowed", signLate.Error!.Code);
        Assert.True(second.IsSuccess, second.Error?.Code);
        Assert.True(accepted.IsSuccess, accepted.Error?.Code);
        Assert.Equal("Expired", await TextAsync($"SELECT status FROM marketplace.property_commitments WHERE id = '{firstCommitment}'"));
        Assert.Equal("PendingSignature", await TextAsync($"SELECT status FROM marketplace.property_commitments WHERE id = '{accepted.Value.CommitmentId}'"));
    }

    [Fact]
    public async Task ASignedCommitmentIsNeverReleasedByTheClock()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        Assert.True((await SetWindowAsync(world.Reviewer, 1, allowed: true)).IsSuccess);
        var commitment = (await RentAndAcceptAsync(world, June1, June1.AddDays(10))).Value.CommitmentId;
        await ConfirmAsync(world.Owner, commitment, CommitmentStep.Signed);
        Clock.Fixed = DateTimeOffset.UtcNow.AddDays(30);
        var again = await PublishAsync(world, ListingPurpose.Rent);

        var overlapping = await RentalRequestAsync(await NewUserAsync(), again.ListingId, RentPeriod.Daily, June1.AddDays(2), June1.AddDays(6), null);

        Assert.Equal("marketplace_requests.dates_unavailable", overlapping.Error!.Code);
        Assert.Equal("Signed", await TextAsync($"SELECT status FROM marketplace.property_commitments WHERE id = '{commitment}'"));
    }

    // ---- the administrator's signing window

    [Fact]
    public async Task TheSigningWindowDefaultsToSeventyTwoHoursAndAnAdministratorCanChangeIt()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);

        var before = await GetWindowAsync(world.Reviewer, allowed: true);
        var changed = await SetWindowAsync(world.Reviewer, 24, allowed: true);
        var after = await GetWindowAsync(world.Reviewer, allowed: true);

        Assert.Equal(72, before.Value.Hours);
        Assert.True(before.Value.IsDefault);
        Assert.Equal(24, changed.Value.Hours);
        Assert.False(after.Value.IsDefault);
        Assert.Equal(24, after.Value.Hours);
    }

    [Fact]
    public async Task ANewAcceptanceUsesTheCurrentWindowAndAnEarlierDeadlineNeverMoves()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        await SetWindowAsync(world.Reviewer, 10, allowed: true);
        var first = (await RentAndAcceptAsync(world, June1, June1.AddDays(5))).Value;
        await SetWindowAsync(world.Reviewer, 100, allowed: true);
        var again = await PublishAsync(world, ListingPurpose.Rent);
        var next = (await RentalRequestAsync(await NewUserAsync(), again.ListingId, RentPeriod.Daily, June1.AddDays(10), June1.AddDays(12), null)).Value;
        var second = (await AcceptAsync(world.Owner, RequestKind.Rental, next.RequestId)).Value;

        Assert.Equal(10, (first.SigningDeadline - first.AcceptedAt).TotalHours, 3);
        Assert.Equal(100, (second.SigningDeadline - second.AcceptedAt).TotalHours, 3);
    }

    [Fact]
    public async Task OnlyAnAdministratorWithThePermissionAndAValidRangeCanSetTheWindow()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);

        var forbidden = await SetWindowAsync(world.Stranger, 24, allowed: false);
        var tooShort = await SetWindowAsync(world.Reviewer, 0, allowed: true);
        var tooLong = await SetWindowAsync(world.Reviewer, 721, allowed: true);

        Assert.Equal("authorization.forbidden", forbidden.Error!.Code);
        Assert.Equal("validation.failed", tooShort.Error!.Code);
        Assert.Equal("validation.failed", tooLong.Error!.Code);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM marketplace.settings"));
    }

    // ---- reads

    [Fact]
    public async Task ACustomerSeesOnlyTheirOwnRequestsAndTheAdvertiserSeesOnlyThoseOnTheirListings()
    {
        var world = await SeedAsync([ListingPurpose.Rent, ListingPurpose.Sale]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var sale = await PublishAsync(world, ListingPurpose.Sale);
        var alice = await NewUserAsync();
        var bob = await NewUserAsync();
        var aliceRental = (await RentalRequestAsync(alice, rent.ListingId, RentPeriod.Daily, June1, June1.AddDays(3), "Hi")).Value;
        await RentalRequestAsync(bob, rent.ListingId, RentPeriod.Daily, June1.AddDays(5), June1.AddDays(7), null);
        await PurchaseRequestAsync(alice, sale.ListingId, 4_000_000m, null);

        var aliceAll = await MyRequestsAsync(alice, null);
        var aliceRentals = await MyRequestsAsync(alice, RequestKind.Rental);
        var bobAll = await MyRequestsAsync(bob, null);
        var stranger = await MyRequestsAsync(world.Stranger, null);
        var received = await ReceivedAsync(world.Owner, null, null);
        var receivedForRent = await ReceivedAsync(world.Owner, null, rent.ListingId);
        var otherOwner = await ReceivedAsync(world.Stranger, null, null);
        var oneOfAlice = await GetMyAsync(alice, RequestKind.Rental, aliceRental.RequestId);
        var bobReadsAlice = await GetMyAsync(bob, RequestKind.Rental, aliceRental.RequestId);

        Assert.Equal(2, aliceAll.TotalCount);
        Assert.Equal(1, aliceRentals.TotalCount);
        Assert.Equal(1, bobAll.TotalCount);
        Assert.Equal(0, stranger.TotalCount);
        Assert.Equal(3, received.TotalCount);
        Assert.Equal(2, receivedForRent.TotalCount);
        Assert.Equal(0, otherOwner.TotalCount);
        Assert.All(received.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.RequesterName)));
        Assert.All(aliceAll.Items, item => Assert.Null(item.RequesterName));
        Assert.Equal("Hi", oneOfAlice.Value.Message);
        Assert.Equal("marketplace_requests.not_found", bobReadsAlice.Error!.Code);
    }

    [Fact]
    public async Task CommitmentsAreVisibleToTheirCustomerAndOwnerOnly()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var customer = await NewUserAsync();
        var request = (await RentalRequestAsync(customer, rent.ListingId, RentPeriod.Daily, June1, June1.AddDays(3), null)).Value;
        var commitment = (await AcceptAsync(world.Owner, RequestKind.Rental, request.RequestId)).Value.CommitmentId;

        var asCustomer = await CommitmentsAsync(customer, CommitmentRole.Customer);
        var asOwner = await CommitmentsAsync(world.Owner, CommitmentRole.Owner);
        var customerAsOwner = await CommitmentsAsync(customer, CommitmentRole.Owner);
        var stranger = await CommitmentsAsync(world.Stranger, CommitmentRole.Customer);
        var strangerOne = await GetCommitmentAsync(world.Stranger, commitment);
        var customerOne = await GetCommitmentAsync(customer, commitment);

        Assert.Equal(1, asCustomer.TotalCount);
        Assert.False(asCustomer.Items.Single().IsOwner);
        Assert.Equal(1, asOwner.TotalCount);
        Assert.True(asOwner.Items.Single().IsOwner);
        Assert.Equal(0, customerAsOwner.TotalCount);
        Assert.Equal(0, stranger.TotalCount);
        Assert.Equal("marketplace_commitments.not_found", strangerOne.Error!.Code);
        Assert.True(customerOne.IsSuccess);
    }

    // ---- helpers

    private async Task<ListingDraftResult> PublishAsync(World world, ListingPurpose purpose)
    {
        var draft = await SubmittedAsync(world, purpose);
        var approved = await ApproveAsync(world, draft.SubmissionId);
        Assert.True(approved.IsSuccess, approved.Error?.Code);

        return draft;
    }

    private async Task<Result<AcceptRequestResult>> RentAndAcceptAsync(World world, DateOnly start, DateOnly end)
    {
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var request = await RentalRequestAsync(await NewUserAsync(), rent.ListingId, RentPeriod.Daily, start, end, null);
        Assert.True(request.IsSuccess, request.Error?.Code);

        return await AcceptAsync(world.Owner, RequestKind.Rental, request.Value.RequestId);
    }

    private async Task<Guid> LatestRequestAsync(string table) =>
        await GuidAsync($"SELECT id FROM marketplace.{table} WHERE status = 'Submitted' ORDER BY submitted_at DESC LIMIT 1");

    private async Task<Guid> NewUserAsync()
    {
        var at = DateTimeOffset.UtcNow.AddDays(-30);
        var user = new User(Guid.NewGuid(), $"{Guid.NewGuid():n}@example.com", "Casey", "Customer", at);
        user.Activate(at);
        await using var context = Host.CreateVerificationContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user.Id;
    }

    private async Task<Result<RequestCreatedResult>> RentalRequestAsync(
        Guid customer, Guid listingId, RentPeriod period, DateOnly start, DateOnly end, string? message)
    {
        await using var scope = Scope();
        var handler = new CreateRentalRequestCommandHandler(
            new FakeCurrentUser(customer), scope.Get<IUserRepository>(), scope.Get<IMarketplaceRequestWriteRepository>(), Clock);

        return await handler.Handle(new CreateRentalRequestCommand(listingId, period, start, end, message));
    }

    private async Task<Result<RequestCreatedResult>> PurchaseRequestAsync(Guid customer, Guid listingId, decimal? offer, string? message)
    {
        await using var scope = Scope();
        var handler = new CreatePurchaseRequestCommandHandler(
            new FakeCurrentUser(customer), scope.Get<IUserRepository>(), scope.Get<IMarketplaceRequestWriteRepository>(), Clock);

        return await handler.Handle(new CreatePurchaseRequestCommand(listingId, offer, message));
    }

    private async Task<Result<RequestDecisionResult>> WithdrawRequestAsync(Guid actor, RequestKind kind, Guid requestId)
    {
        await using var scope = Scope();
        var handler = new WithdrawRequestCommandHandler(
            new FakeCurrentUser(actor), scope.Get<IUserRepository>(), scope.Get<IMarketplaceRequestWriteRepository>(), Clock);

        return await handler.Handle(new WithdrawRequestCommand(kind, requestId));
    }

    private async Task<Result<RequestDecisionResult>> RejectRequestAsync(Guid actor, RequestKind kind, Guid requestId)
    {
        await using var scope = Scope();
        var handler = new RejectRequestCommandHandler(
            new FakeCurrentUser(actor), scope.Get<IUserRepository>(), scope.Get<IMarketplaceRequestWriteRepository>(), Clock);

        return await handler.Handle(new RejectRequestCommand(kind, requestId));
    }

    private async Task<Result<AcceptRequestResult>> AcceptAsync(Guid actor, RequestKind kind, Guid requestId)
    {
        await using var scope = Scope();
        var handler = new AcceptRequestCommandHandler(
            new FakeCurrentUser(actor), scope.Get<IUserRepository>(), scope.Get<IMarketplaceRequestWriteRepository>(),
            scope.Get<IMarketplaceSettingsRepository>(), Clock);

        return await handler.Handle(new AcceptRequestCommand(kind, requestId));
    }

    private async Task<Result<CommitmentStepResult>> ConfirmAsync(Guid actor, Guid commitmentId, CommitmentStep step)
    {
        await using var scope = Scope();
        var handler = new ConfirmCommitmentStepCommandHandler(
            new FakeCurrentUser(actor), scope.Get<IUserRepository>(), scope.Get<IMarketplaceRequestWriteRepository>(), Clock);

        return await handler.Handle(new ConfirmCommitmentStepCommand(commitmentId, step));
    }

    private RequestReadHandlers Reads(ScopeHandle scope, Guid actor) =>
        new(new FakeCurrentUser(actor), scope.Get<IUserRepository>(), scope.Get<IMarketplaceRequestReadRepository>(), Clock);

    private async Task<PagedList<MarketplaceRequestView>> MyRequestsAsync(Guid actor, RequestKind? kind)
    {
        await using var scope = Scope();
        var result = await Reads(scope, actor).Handle(new GetMyRequestsQuery(kind, null, null, null));
        Assert.True(result.IsSuccess, result.Error?.Code);

        return result.Value;
    }

    private async Task<PagedList<MarketplaceRequestView>> ReceivedAsync(Guid actor, RequestKind? kind, Guid? listingId)
    {
        await using var scope = Scope();
        var result = await Reads(scope, actor).Handle(new GetReceivedRequestsQuery(kind, listingId, null, null, null));
        Assert.True(result.IsSuccess, result.Error?.Code);

        return result.Value;
    }

    private async Task<Result<MarketplaceRequestView>> GetMyAsync(Guid actor, RequestKind kind, Guid requestId)
    {
        await using var scope = Scope();

        return await Reads(scope, actor).Handle(new GetMyRequestQuery(kind, requestId));
    }

    private async Task<PagedList<CommitmentView>> CommitmentsAsync(Guid actor, CommitmentRole role)
    {
        await using var scope = Scope();
        var result = await Reads(scope, actor).Handle(new GetMyCommitmentsQuery(role, null, null, null));
        Assert.True(result.IsSuccess, result.Error?.Code);

        return result.Value;
    }

    private async Task<Result<CommitmentView>> GetCommitmentAsync(Guid actor, Guid commitmentId)
    {
        await using var scope = Scope();

        return await Reads(scope, actor).Handle(new GetMyCommitmentQuery(commitmentId));
    }

    private SigningWindowHandlers WindowHandlers(ScopeHandle scope, Guid actor, bool allowed) =>
        new(new FakeCurrentUser(actor), scope.Get<IUserRepository>(), new FakePermissionChecker(allowed),
            scope.Get<IMarketplaceSettingsRepository>(), Clock);

    private async Task<Result<SigningWindowResult>> SetWindowAsync(Guid actor, int hours, bool allowed)
    {
        await using var scope = Scope();

        return await WindowHandlers(scope, actor, allowed).Handle(new SetSigningWindowCommand(hours));
    }

    private async Task<Result<SigningWindowResult>> GetWindowAsync(Guid actor, bool allowed)
    {
        await using var scope = Scope();

        return await WindowHandlers(scope, actor, allowed).Handle(new GetSigningWindowQuery());
    }

    private async Task<decimal> DecimalAsync(string sql) => Convert.ToDecimal(await ScalarObjectAsync(sql));

    private sealed class FakePermissionChecker(bool allowed) : IPermissionChecker
    {
        public Task<AuthorizationDecision> CheckAsync(AuthorizationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(allowed ? AuthorizationDecision.Allowed : AuthorizationDecision.Denied);
    }
}
