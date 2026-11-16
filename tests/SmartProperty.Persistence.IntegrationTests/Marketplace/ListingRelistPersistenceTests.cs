using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Create;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Relist;
using SmartProperty.Application.Marketplace.Requests;
using SmartProperty.Application.Marketplace.Requests.Create;
using SmartProperty.Application.Marketplace.Requests.Decide;
using SmartProperty.Application.Marketplace.Requests.Settings;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

/// <summary>
/// Relisting a closed listing as a copy of its last approved version, by the owner and automatically once the accepted
/// request that closed it has ended, through the real handlers, the real queue query and real PostgreSQL.
/// </summary>
public sealed class ListingRelistPersistenceTests(PostgreSqlFixture fixture) : MarketplaceWorkflowTestBase(fixture)
{
    private static readonly DateOnly June1 = new(2027, 6, 1);

    // ---- what an acceptance records

    [Fact]
    public async Task AnAcceptanceRecordsWhichCommitmentClosedEachListing()
    {
        var world = await SeedAsync([ListingPurpose.Rent, ListingPurpose.Sale]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        var sale = await PublishAsync(world, ListingPurpose.Sale);

        var commitment = (await RentAndAcceptAsync(world, rent.ListingId)).Value.CommitmentId;

        Assert.Equal(commitment, await GuidAsync($"SELECT closed_by_commitment_id FROM marketplace.listings WHERE id = '{rent.ListingId}'"));
        Assert.Equal(commitment, await GuidAsync($"SELECT closed_by_commitment_id FROM marketplace.listings WHERE id = '{sale.ListingId}'"));
    }

    [Fact]
    public async Task TheAutomaticChoiceIsKeptFromTheNewListingAndCanBeChangedLater()
    {
        var world = await SeedAsync([ListingPurpose.Sale]);
        ListingDraftResult created;

        await using (var scope = Scope())
        {
            var handler = new CreateListingDraftCommandHandler(
                new FakeCurrentUser(world.Owner), scope.Get<IUserRepository>(), scope.Get<IListingSubmissionWriteRepository>(),
                new FakeEvidence(), Clock);
            var result = await handler.Handle(new CreateListingDraftCommand(
                world.PropertyId, ListingPurpose.Sale, SaleContent([Guid.NewGuid()]), Relist: RelistMode.Automatic));
            Assert.True(result.IsSuccess, result.Error?.Code);
            created = result.Value;
        }

        Assert.True(await FlagAsync(created.ListingId));

        var stranger = await SetAutomaticAsync(world.Stranger, created.ListingId, false);
        var off = await SetAutomaticAsync(world.Owner, created.ListingId, false);

        Assert.Equal("listing_submissions.not_found", stranger.Error!.Code);
        Assert.True(off.IsSuccess, off.Error?.Code);
        Assert.Equal(RelistMode.Manual, off.Value.Mode);
        Assert.False(await FlagAsync(created.ListingId));
    }

    // ---- the owner relists

    [Fact]
    public async Task TheOwnerRelistsAClosedListingAsACopyAwaitingReviewAndApprovalPublishesIt()
    {
        var world = await SeedAsync([ListingPurpose.Sale]);
        var sale = await PublishAsync(world, ListingPurpose.Sale);
        Assert.True((await WithdrawAsync(world, sale.ListingId)).IsSuccess);

        var relisted = await RelistByOwnerAsync(world.Owner, sale.ListingId);

        Assert.True(relisted.IsSuccess, relisted.Error?.Code);
        Assert.Equal(ListingSubmissionStatus.PendingReview, relisted.Value.Status);
        Assert.Equal(2, relisted.Value.Version);
        var id = relisted.Value.SubmissionId;
        Assert.Equal("Villa for sale", await TextAsync($"SELECT content_snapshot->'Payload'->>'Title' FROM marketplace.submissions WHERE id = '{id}'"));
        Assert.Equal("Withdrawn", await TextAsync($"SELECT publication_status FROM marketplace.listings WHERE id = '{sale.ListingId}'"));

        var approved = await ApproveAsync(world, id);

        Assert.True(approved.IsSuccess, approved.Error?.Code);
        Assert.Equal("Published", await TextAsync($"SELECT publication_status FROM marketplace.listings WHERE id = '{sale.ListingId}'"));
    }

    [Fact]
    public async Task RelistingIsRefusedForSomeoneElsesAnOpenOrAnAlreadyRelistedListing()
    {
        var world = await SeedAsync([ListingPurpose.Sale]);
        var sale = await PublishAsync(world, ListingPurpose.Sale);

        var open = await RelistByOwnerAsync(world.Owner, sale.ListingId);
        await WithdrawAsync(world, sale.ListingId);
        var stranger = await RelistByOwnerAsync(world.Stranger, sale.ListingId);
        var first = await RelistByOwnerAsync(world.Owner, sale.ListingId);
        var second = await RelistByOwnerAsync(world.Owner, sale.ListingId);

        Assert.Equal("listing_submissions.not_relistable", open.Error!.Code);
        Assert.Equal("listing_submissions.not_found", stranger.Error!.Code);
        Assert.True(first.IsSuccess, first.Error?.Code);
        Assert.Equal("listing_submissions.open_revision_exists", second.Error!.Code);
    }

    [Fact]
    public async Task RelistingNeedsTheGrantStillInForce()
    {
        var world = await SeedAsync([ListingPurpose.Sale]);
        var sale = await PublishAsync(world, ListingPurpose.Sale);
        await WithdrawAsync(world, sale.ListingId);
        await RevokeAllGrantsAsync(world);

        var result = await RelistByOwnerAsync(world.Owner, sale.ListingId);

        Assert.Equal("listing_submissions.not_eligible", result.Error!.Code);
        Assert.Equal(1, await CountAsync("marketplace.submissions"));
    }

    [Fact]
    public async Task ARentalCopyLeavesOutWindowsThatHaveEndedAndIsRefusedWhenNoneAreLeft()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        await WithdrawAsync(world, rent.ListingId);

        // The only window is 2027-06-01 to 2027-09-01: after it, nothing is left to offer.
        Clock.Fixed = new DateTimeOffset(2027, 9, 2, 12, 0, 0, TimeSpan.Zero);
        var result = await RelistByOwnerAsync(world.Owner, rent.ListingId);

        Assert.Equal("listing_submissions.relist_dates_passed", result.Error!.Code);
    }

    // ---- automatic relisting

    [Fact]
    public async Task AnExpiredSignatureRelistsAutomaticallyOnceAndRecordsTheExpiry()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        Assert.True((await SetWindowAsync(world.Reviewer, 1)).IsSuccess);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        Assert.True((await SetAutomaticAsync(world.Owner, rent.ListingId, true)).IsSuccess);
        var commitment = (await RentAndAcceptAsync(world, rent.ListingId)).Value.CommitmentId;

        var beforeDeadline = await DueAsync();
        var early = await RelistAutomaticallyAsync(rent.ListingId);
        Clock.Fixed = DateTimeOffset.UtcNow.AddHours(2);
        var due = await DueAsync();
        var relisted = await RelistAutomaticallyAsync(rent.ListingId);
        var dueAfter = await DueAsync();
        var again = await RelistAutomaticallyAsync(rent.ListingId);

        Assert.Empty(beforeDeadline);
        Assert.Equal("listing_submissions.not_relistable", early.Error!.Code);
        Assert.Equal([rent.ListingId], due);
        Assert.True(relisted.IsSuccess, relisted.Error?.Code);
        Assert.Equal(ListingSubmissionStatus.PendingReview, relisted.Value.Status);
        Assert.Equal("Expired", await TextAsync($"SELECT status FROM marketplace.property_commitments WHERE id = '{commitment}'"));
        Assert.Empty(dueAfter);
        Assert.Equal("listing_submissions.open_revision_exists", again.Error!.Code);
    }

    [Fact]
    public async Task AReturnedRentalRelistsAutomaticallyAndACompletedSaleNeverDoes()
    {
        var world = await SeedAsync([ListingPurpose.Rent, ListingPurpose.Sale]);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        await SetAutomaticAsync(world.Owner, rent.ListingId, true);
        var rental = (await RentAndAcceptAsync(world, rent.ListingId)).Value.CommitmentId;
        await ConfirmAsync(world.Owner, rental, CommitmentStep.Signed);
        await ConfirmAsync(world.Owner, rental, CommitmentStep.Handover);

        var whileOccupied = await DueAsync();
        await ConfirmAsync(world.Owner, rental, CommitmentStep.Return);
        var afterReturn = await DueAsync();

        Assert.Empty(whileOccupied);
        Assert.Equal([rent.ListingId], afterReturn);

        // A sale that completes: its listing is never relisted, even with the choice on.
        var sale = await PublishAsync(world, ListingPurpose.Sale);
        await SetAutomaticAsync(world.Owner, sale.ListingId, true);
        var purchase = await PurchaseRequestAsync(await NewUserAsync(), sale.ListingId);
        var sold = (await AcceptAsync(world.Owner, RequestKind.Purchase, purchase)).Value.CommitmentId;
        await ConfirmAsync(world.Owner, sold, CommitmentStep.Signed);
        await ConfirmAsync(world.Owner, sold, CommitmentStep.Handover);

        Assert.DoesNotContain(sale.ListingId, await DueAsync());
        // Sold through the platform: the property itself is unavailable to the seller until the buyer's transfer.
        Assert.Equal("listing_submissions.property_unavailable", (await RelistAutomaticallyAsync(sale.ListingId)).Error!.Code);
    }

    [Fact]
    public async Task WithTheChoiceOffNothingIsRelistedAutomaticallyButTheOwnerStillCan()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        Assert.True((await SetWindowAsync(world.Reviewer, 1)).IsSuccess);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        await RentAndAcceptAsync(world, rent.ListingId);
        Clock.Fixed = DateTimeOffset.UtcNow.AddHours(2);

        var due = await DueAsync();
        var automatic = await RelistAutomaticallyAsync(rent.ListingId);
        var byOwner = await RelistByOwnerAsync(world.Owner, rent.ListingId);

        Assert.Empty(due);
        Assert.Equal("listing_submissions.not_relistable", automatic.Error!.Code);
        Assert.True(byOwner.IsSuccess, byOwner.Error?.Code);
    }

    [Fact]
    public async Task ARejectedAutomaticCopyIsNotSubmittedAgain()
    {
        var world = await SeedAsync([ListingPurpose.Rent]);
        Assert.True((await SetWindowAsync(world.Reviewer, 1)).IsSuccess);
        var rent = await PublishAsync(world, ListingPurpose.Rent);
        await SetAutomaticAsync(world.Owner, rent.ListingId, true);
        await RentAndAcceptAsync(world, rent.ListingId);
        Clock.Fixed = DateTimeOffset.UtcNow.AddHours(2);
        var copy = await RelistAutomaticallyAsync(rent.ListingId);
        Assert.True((await RejectAsync(world, copy.Value.SubmissionId, "Photos are out of date.")).IsSuccess);

        Assert.Empty(await DueAsync());
        Assert.Equal("listing_submissions.not_relistable", (await RelistAutomaticallyAsync(rent.ListingId)).Error!.Code);
    }

    // ---- helpers

    private async Task<ListingDraftResult> PublishAsync(World world, ListingPurpose purpose)
    {
        var draft = await SubmittedAsync(world, purpose);
        var approved = await ApproveAsync(world, draft.SubmissionId);
        Assert.True(approved.IsSuccess, approved.Error?.Code);

        return draft;
    }

    private async Task<Result<AcceptRequestResult>> RentAndAcceptAsync(World world, Guid rentListingId)
    {
        Result<RequestCreatedResult> request;

        await using (var scope = Scope())
        {
            var handler = new CreateRentalRequestCommandHandler(
                new FakeCurrentUser(await NewUserAsync()), scope.Get<IUserRepository>(), scope.Get<IMarketplaceRequestWriteRepository>(), Clock);
            request = await handler.Handle(new CreateRentalRequestCommand(rentListingId, RentPeriod.Daily, June1, June1.AddDays(10), null));
        }

        Assert.True(request.IsSuccess, request.Error?.Code);
        var accepted = await AcceptAsync(world.Owner, RequestKind.Rental, request.Value.RequestId);
        Assert.True(accepted.IsSuccess, accepted.Error?.Code);

        return accepted;
    }

    private async Task<Guid> PurchaseRequestAsync(Guid customer, Guid listingId)
    {
        await using var scope = Scope();
        var handler = new CreatePurchaseRequestCommandHandler(
            new FakeCurrentUser(customer), scope.Get<IUserRepository>(), scope.Get<IMarketplaceRequestWriteRepository>(), Clock);
        var result = await handler.Handle(new CreatePurchaseRequestCommand(listingId, 4_900_000m, null));
        Assert.True(result.IsSuccess, result.Error?.Code);

        return result.Value.RequestId;
    }

    private async Task<Result<AcceptRequestResult>> AcceptAsync(Guid actor, RequestKind kind, Guid requestId)
    {
        await using var scope = Scope();
        var handler = new AcceptRequestCommandHandler(
            new FakeCurrentUser(actor), scope.Get<IUserRepository>(), scope.Get<IMarketplaceRequestWriteRepository>(),
            scope.Get<IMarketplaceSettingsRepository>(), Clock);

        return await handler.Handle(new AcceptRequestCommand(kind, requestId));
    }

    private async Task ConfirmAsync(Guid actor, Guid commitmentId, CommitmentStep step)
    {
        await using var scope = Scope();
        var handler = new ConfirmCommitmentStepCommandHandler(
            new FakeCurrentUser(actor), scope.Get<IUserRepository>(), scope.Get<IMarketplaceRequestWriteRepository>(), Clock);
        var result = await handler.Handle(new ConfirmCommitmentStepCommand(commitmentId, step));
        Assert.True(result.IsSuccess, result.Error?.Code);
    }

    private async Task<Result<SigningWindowResult>> SetWindowAsync(Guid actor, int hours)
    {
        await using var scope = Scope();
        var handler = new SigningWindowHandlers(
            new FakeCurrentUser(actor), scope.Get<IUserRepository>(), new AllowAll(),
            scope.Get<IMarketplaceSettingsRepository>(), Clock);

        return await handler.Handle(new SetSigningWindowCommand(hours));
    }

    private async Task<Result<RelistListingResult>> RelistByOwnerAsync(Guid actor, Guid listingId)
    {
        await using var scope = Scope();
        var handler = new RelistListingCommandHandler(new FakeCurrentUser(actor), scope.Get<IUserRepository>(), Relister(scope));

        return await handler.Handle(new RelistListingCommand(listingId));
    }

    private async Task<Result<RelistListingResult>> RelistAutomaticallyAsync(Guid listingId)
    {
        await using var scope = Scope();

        return await Relister(scope).RelistAutomaticallyAsync(listingId);
    }

    private async Task<Result<RelistModeResult>> SetAutomaticAsync(Guid actor, Guid listingId, bool enabled)
    {
        await using var scope = Scope();
        var handler = new SetRelistModeCommandHandler(
            new FakeCurrentUser(actor), scope.Get<IUserRepository>(), scope.Get<IListingSubmissionWriteRepository>());

        return await handler.Handle(new SetRelistModeCommand(listingId, enabled ? RelistMode.Automatic : RelistMode.Manual));
    }

    private async Task<IReadOnlyList<Guid>> DueAsync()
    {
        await using var scope = Scope();

        return await scope.Get<IAutomaticRelistQueue>().GetDueListingIdsAsync(Clock.UtcNow, 100);
    }

    private ListingRelister Relister(ScopeHandle scope) =>
        new(scope.Get<IMarketplaceRequestWriteRepository>(), scope.Get<IUserRepository>(), new FakeEvidence(), Clock);

    private async Task<bool> FlagAsync(Guid listingId) =>
        (bool)(await ScalarObjectAsync($"SELECT relist_automatically FROM marketplace.listings WHERE id = '{listingId}'"))!;

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

    private sealed class AllowAll : IPermissionChecker
    {
        public Task<AuthorizationDecision> CheckAsync(AuthorizationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(AuthorizationDecision.Allowed);
    }
}
