using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartProperty.Application.Abstractions.Identity;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Create;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Discard;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Submit;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Update;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Locations;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Marketplace;

/// <summary>
/// The real write repository against real PostgreSQL, driven by the real draft handlers. Grants are seeded
/// explicitly through the Domain as test fixtures: nothing in the product creates one yet. The evidence provider here
/// is a test fake; production registers one that never answers.
/// </summary>
public sealed class ListingSubmissionWriteRepositoryTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    // ---- the locking contract

    [Fact]
    public async Task LockingAPropertyReturnsItsCanonicalFactsAndAMissingOneIsNull()
    {
        var world = await SeedAsync();
        await using var scope = Scope();
        var repository = scope.Get<IListingSubmissionWriteRepository>();

        var view = await repository.LockPropertyAsync(world.PropertyId);
        var missing = await Scope().Get<IListingSubmissionWriteRepository>().LockPropertyAsync(Guid.NewGuid());

        Assert.NotNull(view);
        Assert.Equal(world.PropertyId, view.PropertyId);
        Assert.True(view.IsActive);
        Assert.False(view.HasMarketplaceReservation);
        Assert.Equal(PropertyType.Villa, view.PropertyType);
        Assert.Equal("EG", view.CountryCode);
        Assert.Equal("Cairo Governorate", view.Region);
        Assert.Equal("Cairo", view.City);
        Assert.Equal("Maadi", view.District);
        Assert.Null(missing);
    }

    [Fact]
    public async Task AnArchivedPropertyIsReportedAsNotActive()
    {
        var world = await SeedAsync(archive: true);
        await using var scope = Scope();

        var view = await scope.Get<IListingSubmissionWriteRepository>().LockPropertyAsync(world.PropertyId);

        Assert.False(view!.IsActive);
    }

    [Fact]
    public async Task ACallThatNeedsALockNotYetTakenThrowsInsteadOfTakingItOutOfOrder()
    {
        var world = await SeedAsync();
        await using var scope = Scope();
        var repository = scope.Get<IListingSubmissionWriteRepository>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.GetGrantForSharedLockAsync(world.Owner, world.PropertyId, ListingPurpose.Sale));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.GetListingAsync(world.PropertyId, ListingPurpose.Sale));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetLatestSubmissionAsync(Guid.NewGuid()));

        await repository.LockPropertyAsync(world.PropertyId);

        // The grant must be read before the listing is locked.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.GetListingAsync(world.PropertyId, ListingPurpose.Sale));
        // A grant is read under the lock of its own property only.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.GetGrantForSharedLockAsync(world.Owner, Guid.NewGuid(), ListingPurpose.Sale));
    }

    [Fact]
    public async Task OneUnitOfWorkLocksOnlyOneProperty()
    {
        var first = await SeedAsync();
        var second = await SeedAsync();
        await using var scope = Scope();
        var repository = scope.Get<IListingSubmissionWriteRepository>();
        await repository.LockPropertyAsync(first.PropertyId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.LockPropertyAsync(second.PropertyId));
    }

    [Fact]
    public async Task ThePropertyRowStaysLockedUntilTheUnitIsReleased()
    {
        var world = await SeedAsync();
        var scope = Scope();
        await scope.Get<IListingSubmissionWriteRepository>().LockPropertyAsync(world.PropertyId);

        var whileHeld = await TryLockAsync($"SELECT 1 FROM registry.properties WHERE id = '{world.PropertyId}' FOR UPDATE NOWAIT");
        await scope.DisposeAsync();
        var afterRelease = await TryLockAsync($"SELECT 1 FROM registry.properties WHERE id = '{world.PropertyId}' FOR UPDATE NOWAIT");

        Assert.Equal(PostgresErrorCodes.LockNotAvailable, whileHeld);
        Assert.Null(afterRelease);
    }

    [Fact]
    public async Task TheGrantIsHeldSharedSoARevocationCannotCommitUnderneath()
    {
        var world = await SeedAsync();
        var grantId = await SeedGrantAsync(world, ListingPurpose.Sale);
        var scope = Scope();
        var repository = scope.Get<IListingSubmissionWriteRepository>();
        await repository.LockPropertyAsync(world.PropertyId);
        await repository.GetGrantForSharedLockAsync(world.Owner, world.PropertyId, ListingPurpose.Sale);

        var exclusive = await TryLockAsync($"SELECT 1 FROM marketplace.authority_grants WHERE id = '{grantId}' FOR UPDATE NOWAIT");
        var shared = await TryLockAsync($"SELECT 1 FROM marketplace.authority_grants WHERE id = '{grantId}' FOR SHARE NOWAIT");
        await scope.DisposeAsync();
        var afterRelease = await TryLockAsync($"SELECT 1 FROM marketplace.authority_grants WHERE id = '{grantId}' FOR UPDATE NOWAIT");

        Assert.Equal(PostgresErrorCodes.LockNotAvailable, exclusive);
        Assert.Null(shared);
        Assert.Null(afterRelease);
    }

    // ---- reading grants

    [Fact]
    public async Task AGrantIsReadWithItsSubjectValidityAndProvenance()
    {
        var world = await SeedAsync();
        var grantId = await SeedGrantAsync(world, ListingPurpose.Sale);
        await using var scope = Scope();
        var repository = scope.Get<IListingSubmissionWriteRepository>();
        await repository.LockPropertyAsync(world.PropertyId);

        var grant = await repository.GetGrantForSharedLockAsync(world.Owner, world.PropertyId, ListingPurpose.Sale);

        Assert.NotNull(grant);
        Assert.Equal(grantId, grant.GrantId);
        Assert.Equal(world.Owner, grant.GranteeUserId);
        Assert.Equal(world.PropertyId, grant.PropertyId);
        Assert.True(grant.AllowsSale);
        Assert.False(grant.AllowsRent);
        Assert.Equal(AuthorityGrantState.Active, grant.State);
        Assert.Equal(ListingRepresentationKind.Owner, grant.Representation);
        Assert.Equal(world.Owner, grant.LinkedUserId);
        Assert.Equal(world.Issuer, grant.IssuedByUserId);
        Assert.Equal(world.LegalReviewer, grant.LegalReviewedByUserId);
        Assert.True(grant.IssuerWasAuthorized);
        Assert.True(grant.ValidUntil > grant.ValidFrom);
        Assert.Equal(1, grant.Version);
    }

    [Fact]
    public async Task GrantsArePurposeSpecificSoASaleGrantNeverAnswersForRent()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);
        await using var scope = Scope();
        var repository = scope.Get<IListingSubmissionWriteRepository>();
        await repository.LockPropertyAsync(world.PropertyId);

        var rent = await repository.GetGrantForSharedLockAsync(world.Owner, world.PropertyId, ListingPurpose.Rent);

        Assert.Null(rent);
    }

    [Fact]
    public async Task AnotherUsersGrantIsNeverReturned()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);
        await using var scope = Scope();
        var repository = scope.Get<IListingSubmissionWriteRepository>();
        await repository.LockPropertyAsync(world.PropertyId);

        var other = await repository.GetGrantForSharedLockAsync(world.Stranger, world.PropertyId, ListingPurpose.Sale);

        Assert.Null(other);
    }

    [Fact]
    public async Task ARevokedGrantIsReportedAsRevokedAndAFreshOneWinsOverIt()
    {
        var world = await SeedAsync();
        var first = await SeedGrantAsync(world, ListingPurpose.Sale);
        await RevokeAsync(first, world);
        await using (var scope = Scope())
        {
            var repository = scope.Get<IListingSubmissionWriteRepository>();
            await repository.LockPropertyAsync(world.PropertyId);
            var revoked = await repository.GetGrantForSharedLockAsync(world.Owner, world.PropertyId, ListingPurpose.Sale);

            Assert.Equal(first, revoked!.GrantId);
            Assert.Equal(AuthorityGrantState.Revoked, revoked.State);
            Assert.NotNull(revoked.RevokedAt);
        }

        var second = await SeedGrantAsync(world, ListingPurpose.Sale);
        await using (var scope = Scope())
        {
            var repository = scope.Get<IListingSubmissionWriteRepository>();
            await repository.LockPropertyAsync(world.PropertyId);
            var current = await repository.GetGrantForSharedLockAsync(world.Owner, world.PropertyId, ListingPurpose.Sale);

            Assert.Equal(second, current!.GrantId);
            Assert.Equal(AuthorityGrantState.Active, current.State);
        }
    }

    // ---- drafts through the real handlers

    [Fact]
    public async Task AValidSaleGrantLetsTheOwnerCreateADraftAndNothingIsPublished()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);

        var result = await CreateAsync(world, ListingPurpose.Sale);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(1, result.Value.Version);
        Assert.Equal(1, result.Value.Revision);
        var stored = await ReadSubmissionAsync(result.Value.SubmissionId);
        Assert.Equal("Draft", stored.Status);
        Assert.Equal(world.PropertyId, stored.PropertyId);
        var listing = await ReadListingAsync(result.Value.ListingId);
        Assert.Equal("Unpublished", listing.Publication);
        Assert.Null(listing.PublishedSubmissionId);
    }

    [Fact]
    public async Task TheLocationAndTypeComeFromTheRegistryNotTheRequest()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);

        var result = await CreateAsync(world, ListingPurpose.Sale);

        await using var context = Host.CreateVerificationContext();
        var submission = await context.ListingSubmissions.AsNoTracking().SingleAsync(s => s.Id == result.Value.SubmissionId);
        Assert.Equal("Maadi", submission.Content.District);
        Assert.Equal("Cairo", submission.Content.City);
        Assert.Equal(PropertyType.Villa, submission.Content.PropertyType);
        Assert.Equal(world.Owner, submission.Authority.ActingUserId);
    }

    [Fact]
    public async Task WithoutAGrantTheOwnerGetsAClearAuthorizationErrorAndNothingIsWritten()
    {
        var world = await SeedAsync();

        var result = await CreateAsync(world, ListingPurpose.Sale);

        Assert.True(result.IsFailure);
        Assert.Equal("listing_submissions.not_eligible", result.Error!.Code);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
        Assert.Equal(0, await CountAsync("marketplace.listings"));
        Assert.Equal(0, await CountAsync("marketplace.submissions"));
    }

    [Fact]
    public async Task ARevokedGrantIsNotEligible()
    {
        var world = await SeedAsync();
        await RevokeAsync(await SeedGrantAsync(world, ListingPurpose.Sale), world);

        var result = await CreateAsync(world, ListingPurpose.Sale);

        Assert.Equal("listing_submissions.not_eligible", result.Error!.Code);
        Assert.Equal(0, await CountAsync("marketplace.listings"));
    }

    [Fact]
    public async Task ASaleGrantDoesNotAuthorizeARentDraft()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);

        var result = await CreateAsync(world, ListingPurpose.Rent);

        Assert.Equal("listing_submissions.not_eligible", result.Error!.Code);
        Assert.Equal(0, await CountAsync("marketplace.listings"));
    }

    [Fact]
    public async Task AnotherUserWithoutAGrantCannotCreateADraftForThePropertyEvenIfTheOwnerHasOne()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);

        var result = await CreateAsync(world, ListingPurpose.Sale, actor: world.Stranger);

        Assert.Equal("listing_submissions.not_eligible", result.Error!.Code);
        Assert.Equal(0, await CountAsync("marketplace.listings"));
    }

    [Fact]
    public async Task ASecondDraftWhileOneIsOpenIsRefused()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);
        Assert.True((await CreateAsync(world, ListingPurpose.Sale)).IsSuccess);

        var second = await CreateAsync(world, ListingPurpose.Sale);

        Assert.Equal("listing_submissions.open_revision_exists", second.Error!.Code);
        Assert.Equal(1, await CountAsync("marketplace.submissions"));
    }

    [Fact]
    public async Task TwoSimultaneousCreatesLeaveExactlyOneDraft()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);

        var results = await Task.WhenAll(
            CreateAsync(world, ListingPurpose.Sale),
            CreateAsync(world, ListingPurpose.Sale),
            CreateAsync(world, ListingPurpose.Sale));

        Assert.Equal(1, results.Count(result => result.IsSuccess));
        Assert.All(results.Where(result => result.IsFailure), result =>
            Assert.Equal("listing_submissions.open_revision_exists", result.Error!.Code));
        Assert.Equal(1, await CountAsync("marketplace.listings"));
        Assert.Equal(1, await CountAsync("marketplace.submissions"));
    }

    [Fact]
    public async Task UpdatingADraftBumpsTheRevisionAndAStaleRevisionIsRefused()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);
        var draft = (await CreateAsync(world, ListingPurpose.Sale)).Value;

        var updated = await UpdateAsync(world, draft.SubmissionId, draft.Revision, "A new title");
        var stale = await UpdateAsync(world, draft.SubmissionId, draft.Revision, "Too late");

        Assert.True(updated.IsSuccess, updated.Error?.Code);
        Assert.Equal(2, updated.Value.Revision);
        Assert.Equal("listing_submissions.stale_revision", stale.Error!.Code);
        await using var context = Host.CreateVerificationContext();
        var stored = await context.ListingSubmissions.AsNoTracking().SingleAsync(s => s.Id == draft.SubmissionId);
        Assert.Equal("A new title", stored.Content.Title);
        Assert.Equal(2, stored.Revision);
    }

    [Fact]
    public async Task AnotherUsersDraftCannotBeUpdated()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);
        var draft = (await CreateAsync(world, ListingPurpose.Sale)).Value;

        var result = await UpdateAsync(world, draft.SubmissionId, draft.Revision, "Hijacked", actor: world.Stranger);

        Assert.Equal("listing_submissions.not_found", result.Error!.Code);
    }

    [Fact]
    public async Task UpdatingAfterTheGrantIsRevokedIsRefused()
    {
        var world = await SeedAsync();
        var grantId = await SeedGrantAsync(world, ListingPurpose.Sale);
        var draft = (await CreateAsync(world, ListingPurpose.Sale)).Value;
        await RevokeAsync(grantId, world);

        var result = await UpdateAsync(world, draft.SubmissionId, draft.Revision, "After revocation");

        Assert.Equal("listing_submissions.not_eligible", result.Error!.Code);
    }

    [Fact]
    public async Task DiscardingADraftRemovesItsEmptyListingAndWritesTheAuditRowAtomically()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);
        var draft = (await CreateAsync(world, ListingPurpose.Sale)).Value;

        var result = await DiscardAsync(world, draft.SubmissionId, draft.Revision);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.True(result.Value.ListingRemoved);
        Assert.Equal(0, await CountAsync("marketplace.submissions"));
        Assert.Equal(0, await CountAsync("marketplace.listings"));
        Assert.Equal(1, await CountAsync("marketplace.draft_discard_audits"));
        await using var context = Host.CreateVerificationContext();
        var audit = await context.Set<SmartProperty.Persistence.Entities.Marketplace.ListingDraftDiscardAudit>()
            .AsNoTracking().SingleAsync();
        Assert.Equal(draft.SubmissionId, audit.SubmissionId);
        Assert.Equal(world.Owner, audit.ActorUserId);
        Assert.Equal(world.PropertyId, audit.PropertyId);
    }

    [Fact]
    public async Task AnotherUsersDraftCannotBeDiscarded()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);
        var draft = (await CreateAsync(world, ListingPurpose.Sale)).Value;

        var result = await DiscardAsync(world, draft.SubmissionId, draft.Revision, actor: world.Stranger);

        Assert.Equal("listing_submissions.not_found", result.Error!.Code);
        Assert.Equal(1, await CountAsync("marketplace.submissions"));
        Assert.Equal(0, await CountAsync("marketplace.draft_discard_audits"));
    }

    // ---- evidence stays fail-closed

    [Fact]
    public async Task ADraftWithEvidenceIdsFailsClearlyWhileTheProviderIsUnavailable()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);
        var evidence = new FakeEvidence { Available = false };

        var result = await CreateAsync(world, ListingPurpose.Sale, evidence: evidence, evidenceIds: [Guid.NewGuid()]);

        Assert.Equal("listing_submissions.evidence_verification_unavailable", result.Error!.Code);
        Assert.Equal(ErrorType.ServiceUnavailable, result.Error.Type);
        Assert.Equal(0, await CountAsync("marketplace.submissions"));
    }

    [Fact]
    public async Task ADraftWithoutEvidenceNeverAsksTheProviderSoDraftingWorksWhileItIsUnavailable()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);
        var evidence = new FakeEvidence { Available = false };

        var result = await CreateAsync(world, ListingPurpose.Sale, evidence: evidence);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(0, evidence.ResolveCalls);
    }

    [Fact]
    public async Task SubmissionFailsClearlyWhileTheProviderIsUnavailableAndTheDraftStaysADraft()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);
        var available = new FakeEvidence();
        var draft = (await CreateAsync(world, ListingPurpose.Sale, evidence: available, evidenceIds: [Guid.NewGuid()])).Value;

        var result = await SubmitAsync(world, draft.SubmissionId, draft.Revision, new FakeEvidence { Available = false });

        Assert.Equal("listing_submissions.evidence_verification_unavailable", result.Error!.Code);
        var stored = await ReadSubmissionAsync(draft.SubmissionId);
        Assert.Equal("Draft", stored.Status);
        Assert.Null(stored.SubmittedAt);
    }

    [Fact]
    public async Task SubmissionWithCleanEvidenceMovesTheDraftToPendingReviewAndPublishesNothing()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Sale);
        var evidence = new FakeEvidence();
        var draft = (await CreateAsync(world, ListingPurpose.Sale, evidence: evidence, evidenceIds: [Guid.NewGuid()])).Value;

        var result = await SubmitAsync(world, draft.SubmissionId, draft.Revision, evidence);

        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(ListingSubmissionStatus.PendingReview, result.Value.Status);
        Assert.Equal("PendingReview", (await ReadSubmissionAsync(draft.SubmissionId)).Status);
        Assert.Equal("Unpublished", (await ReadListingAsync(draft.ListingId)).Publication);
    }

    // ---- rentals with pricing options

    [Fact]
    public async Task ARentalDraftKeepsEveryPricingOptionAndTheTermsWithTheCountryTimeZone()
    {
        var world = await SeedAsync(placeCountryCode: "EG");
        await SeedGrantAsync(world, ListingPurpose.Rent);

        var result = await CreateRentalAsync(world);

        Assert.True(result.IsSuccess, result.Error?.Code);
        await using var context = Host.CreateVerificationContext();
        var content = (await context.ListingSubmissions.AsNoTracking().SingleAsync(s => s.Id == result.Value.SubmissionId)).Content;
        Assert.Equal(ListingPurpose.Rent, content.Purpose);
        Assert.Equal(4, content.PricingOptions.Count);
        Assert.Equal(1_000m, content.PricingOptions.Single(o => o.Period == RentPeriod.Daily).Amount);
        Assert.Equal(6_000m, content.PricingOptions.Single(o => o.Period == RentPeriod.Weekly).Amount);
        Assert.Equal(20_000m, content.PricingOptions.Single(o => o.Period == RentPeriod.Monthly).Amount);
        Assert.Equal(200_000m, content.PricingOptions.Single(o => o.Period == RentPeriod.Yearly).Amount);
        Assert.All(content.PricingOptions, option => Assert.Equal("EGP", option.Currency));
        Assert.Equal("Africa/Cairo", content.RentalTerms!.TimeZoneId);
        Assert.Equal(new TimeOnly(14, 0), content.RentalTerms.CheckInTime);
        Assert.Equal(new TimeOnly(11, 0), content.RentalTerms.CheckOutTime);
        Assert.Equal(3, content.RentalTerms.MinimumStayDays);
        Assert.Equal(2, content.RentalTerms.Windows.Count);
        Assert.Equal(new DateOnly(2027, 6, 1), content.RentalTerms.Windows[0].StartDate);
        // The plain price a single-price reader sees is the monthly option.
        Assert.Equal(RentPeriod.Monthly, content.RentPeriod);
        Assert.Equal(20_000m, content.Amount);
    }

    [Fact]
    public async Task ThePropertysOwnTimeZoneWinsOverItsCountrys()
    {
        var world = await SeedAsync(placeCountryCode: "EG", propertyTimeZoneId: "Asia/Riyadh");
        await SeedGrantAsync(world, ListingPurpose.Rent);

        var result = await CreateRentalAsync(world);

        Assert.True(result.IsSuccess, result.Error?.Code);
        await using var context = Host.CreateVerificationContext();
        var content = (await context.ListingSubmissions.AsNoTracking().SingleAsync(s => s.Id == result.Value.SubmissionId)).Content;
        Assert.Equal("Asia/Riyadh", content.RentalTerms!.TimeZoneId);
    }

    [Fact]
    public async Task ThePropertysOwnTimeZoneMakesARentalPossibleInACountryWithNone()
    {
        var world = await SeedAsync(placeCountryCode: "ZZ", propertyTimeZoneId: "Africa/Cairo");
        await SeedGrantAsync(world, ListingPurpose.Rent);

        var result = await CreateRentalAsync(world);

        Assert.True(result.IsSuccess, result.Error?.Code);
    }

    [Fact]
    public async Task ARentalNeedsARentGrantNotASaleGrant()
    {
        var world = await SeedAsync(placeCountryCode: "EG");
        await SeedGrantAsync(world, ListingPurpose.Sale);

        var result = await CreateRentalAsync(world);

        Assert.Equal("listing_submissions.not_eligible", result.Error!.Code);
        Assert.Equal(0, await CountAsync("marketplace.submissions"));
    }

    [Fact]
    public async Task ARentalInACountryWithNoTimeZoneIsRefusedClearlyAndNothingIsWritten()
    {
        var world = await SeedAsync(placeCountryCode: "ZZ");
        await SeedGrantAsync(world, ListingPurpose.Rent);

        var result = await CreateRentalAsync(world);

        Assert.Equal("listing_submissions.rental_time_zone_unavailable", result.Error!.Code);
        Assert.Equal(0, await CountAsync("marketplace.listings"));
        Assert.Equal(0, await CountAsync("marketplace.submissions"));
    }

    [Fact]
    public async Task ARentalOnAPropertyWithNoPlaceIsRefusedBecauseItHasNoTimeZone()
    {
        var world = await SeedAsync();
        await SeedGrantAsync(world, ListingPurpose.Rent);

        var result = await CreateRentalAsync(world);

        Assert.Equal("listing_submissions.rental_time_zone_unavailable", result.Error!.Code);
    }

    [Fact]
    public async Task ASaleInACountryWithNoTimeZoneIsStillFine()
    {
        var world = await SeedAsync(placeCountryCode: "ZZ");
        await SeedGrantAsync(world, ListingPurpose.Sale);

        var result = await CreateAsync(world, ListingPurpose.Sale);

        Assert.True(result.IsSuccess, result.Error?.Code);
    }

    [Fact]
    public async Task EditingARentalDraftReplacesItsOptions()
    {
        var world = await SeedAsync(placeCountryCode: "EG");
        await SeedGrantAsync(world, ListingPurpose.Rent);
        var draft = (await CreateRentalAsync(world)).Value;
        var fewer = RentalContent("Cheaper", [new RentPricingOptionInput(RentPeriod.Daily, 700m, "EGP")]);

        var updated = await UpdateWithContentAsync(world, draft.SubmissionId, draft.Revision, fewer);

        Assert.True(updated.IsSuccess, updated.Error?.Code);
        await using var context = Host.CreateVerificationContext();
        var content = (await context.ListingSubmissions.AsNoTracking().SingleAsync(s => s.Id == draft.SubmissionId)).Content;
        var only = Assert.Single(content.PricingOptions);
        Assert.Equal(RentPeriod.Daily, only.Period);
        Assert.Equal(700m, only.Amount);
        Assert.Equal(RentPeriod.Daily, content.RentPeriod);
        Assert.Equal("Cheaper", content.Title);
    }

    [Fact]
    public async Task ARentalStatedWithOneSinglePriceStillWorksAndHasNoOptionsOrTerms()
    {
        var world = await SeedAsync(placeCountryCode: "EG");
        await SeedGrantAsync(world, ListingPurpose.Rent);

        var result = await CreateAsync(world, ListingPurpose.Rent);

        Assert.True(result.IsSuccess, result.Error?.Code);
        await using var context = Host.CreateVerificationContext();
        var content = (await context.ListingSubmissions.AsNoTracking().SingleAsync(s => s.Id == result.Value.SubmissionId)).Content;
        Assert.Empty(content.PricingOptions);
        Assert.Null(content.RentalTerms);
        Assert.Equal(RentPeriod.Monthly, content.RentPeriod);
    }

    // ---- helpers

    private sealed record World(Guid PropertyId, Guid Owner, Guid Issuer, Guid LegalReviewer, Guid Stranger);

    private sealed record SubmissionRow(string Status, Guid PropertyId, DateTimeOffset? SubmittedAt);

    private sealed record ListingRow(string Publication, Guid? PublishedSubmissionId);

    private ScopeHandle Scope() => new(Host.CreateScope());

    private async Task<World> SeedAsync(bool archive = false, string? placeCountryCode = null, string? propertyTimeZoneId = null)
    {
        var at = Now.AddDays(-30);
        var users = Enumerable.Range(0, 4)
            .Select(_ => new User(Guid.NewGuid(), $"{Guid.NewGuid():n}@example.com", "Test", "User", at))
            .ToArray();
        foreach (var user in users)
        {
            user.Activate(at);
        }

        // A property with a place has the time zone of its country; one without a place has none.
        Area? area = null;

        if (placeCountryCode is not null)
        {
            var aiId = Random.Shared.Next(10_000_000, 2_000_000_000);
            var country = new Country(Guid.NewGuid(), aiId, $"Country {aiId}", "بلد", placeCountryCode);
            var city = new City(Guid.NewGuid(), aiId, country, "Cairo", "القاهرة");
            area = new Area(Guid.NewGuid(), aiId, city, "Maadi", "المعادي");
        }

        var property = new Property(
            Guid.NewGuid(),
            PropertyType.Villa,
            new PropertyAddress(placeCountryCode ?? "EG", city: "Cairo", region: "Cairo Governorate", district: "Maadi"),
            at,
            physical: propertyTimeZoneId is null ? null : new PropertyPhysicalInfo(timeZoneId: propertyTimeZoneId),
            area: area);

        if (archive)
        {
            property.Archive(at.AddMinutes(1));
        }

        await using var context = Host.CreateVerificationContext();
        context.Users.AddRange(users);

        if (area is not null)
        {
            context.Areas.Add(area);
        }

        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return new World(property.Id, users[0].Id, users[1].Id, users[2].Id, users[3].Id);
    }

    // An explicitly seeded test grant. It goes through the Domain's own Issue so it satisfies every rule a real one
    // would, but no product code path issues grants.
    private async Task<Guid> SeedGrantAsync(World world, ListingPurpose purpose)
    {
        var owner = new ListingOwnerIdentity(ListingOwnerKind.Individual, Guid.NewGuid(), world.Owner);
        var authority = new ListingLegalAuthority(
            world.PropertyId, purpose, owner, world.Owner, ListingRepresentationKind.Owner, null,
            Now.AddDays(-20), Now.AddDays(300));
        ListingReviewContext Reviewer(Guid id) => new(id, world.Owner, owner, null, true, true, null, false);
        var grant = PropertyAuthorityGrant.Issue(
            Guid.NewGuid(), authority, Reviewer(world.Issuer), Guid.NewGuid(), Reviewer(world.LegalReviewer),
            Now.AddDays(-6), Now.AddDays(-5), legalAuthorityVerified: true);

        await using var context = Host.CreateVerificationContext();
        context.PropertyAuthorityGrants.Add(grant);
        await context.SaveChangesAsync();

        return grant.Id;
    }

    private async Task RevokeAsync(Guid grantId, World world)
    {
        await using var context = Host.CreateVerificationContext();
        var grant = await context.PropertyAuthorityGrants.SingleAsync(g => g.Id == grantId);
        var reviewer = new ListingReviewContext(world.Stranger, grant.GranteeUserId, grant.Authority.DeclaredOwner, null, true, true, null, false);
        grant.Revoke(grant.Version, reviewer, Now.AddDays(-1));
        await context.SaveChangesAsync();
    }

    private static ListingDraftContentInput Content(string title, Guid[]? evidenceIds, ListingPurpose purpose) =>
        new(title, "A description", 1_000_000m, "EGP", purpose == ListingPurpose.Rent ? RentPeriod.Monthly : null,
            evidenceIds ?? []);

    private async Task<Result<ListingDraftResult>> CreateAsync(
        World world, ListingPurpose purpose, Guid? actor = null, FakeEvidence? evidence = null, Guid[]? evidenceIds = null)
    {
        await using var scope = Scope();
        var handler = new CreateListingDraftCommandHandler(
            new FakeCurrentUser(actor ?? world.Owner),
            scope.Get<IUserRepository>(),
            scope.Get<IListingSubmissionWriteRepository>(),
            evidence ?? new FakeEvidence(),
            new FakeClock());

        return await handler.Handle(new CreateListingDraftCommand(world.PropertyId, purpose, Content("A title", evidenceIds, purpose)));
    }

    private async Task<Result<ListingDraftResult>> UpdateAsync(
        World world, Guid submissionId, int expectedRevision, string title, Guid? actor = null)
    {
        await using var scope = Scope();
        var handler = new UpdateListingDraftCommandHandler(
            new FakeCurrentUser(actor ?? world.Owner),
            scope.Get<IUserRepository>(),
            scope.Get<IListingSubmissionWriteRepository>(),
            new FakeEvidence(),
            new FakeClock());

        return await handler.Handle(new UpdateListingDraftCommand(
            submissionId, expectedRevision, Content(title, null, ListingPurpose.Sale)));
    }

    private static ListingDraftContentInput RentalContent(string title, RentPricingOptionInput[]? options = null) =>
        new(title, "A rental", 0m, string.Empty, null, [],
            options ??
            [
                new RentPricingOptionInput(RentPeriod.Daily, 1_000m, "EGP"),
                new RentPricingOptionInput(RentPeriod.Weekly, 6_000m, "EGP"),
                new RentPricingOptionInput(RentPeriod.Monthly, 20_000m, "EGP"),
                new RentPricingOptionInput(RentPeriod.Yearly, 200_000m, "EGP"),
            ],
            new RentalTermsInput(
                new TimeOnly(14, 0),
                new TimeOnly(11, 0),
                [
                    new RentalWindowInput(new DateOnly(2027, 6, 1), new DateOnly(2027, 9, 1)),
                    new RentalWindowInput(new DateOnly(2027, 12, 20), new DateOnly(2028, 1, 10)),
                ],
                MinimumStayDays: 3));

    private async Task<Result<ListingDraftResult>> CreateRentalAsync(World world)
    {
        await using var scope = Scope();
        var handler = new CreateListingDraftCommandHandler(
            new FakeCurrentUser(world.Owner),
            scope.Get<IUserRepository>(),
            scope.Get<IListingSubmissionWriteRepository>(),
            new FakeEvidence(),
            new FakeClock());

        return await handler.Handle(new CreateListingDraftCommand(world.PropertyId, ListingPurpose.Rent, RentalContent("A rental")));
    }

    private async Task<Result<ListingDraftResult>> UpdateWithContentAsync(
        World world, Guid submissionId, int expectedRevision, ListingDraftContentInput content)
    {
        await using var scope = Scope();
        var handler = new UpdateListingDraftCommandHandler(
            new FakeCurrentUser(world.Owner),
            scope.Get<IUserRepository>(),
            scope.Get<IListingSubmissionWriteRepository>(),
            new FakeEvidence(),
            new FakeClock());

        return await handler.Handle(new UpdateListingDraftCommand(submissionId, expectedRevision, content));
    }

    private async Task<Result<DiscardListingDraftResult>> DiscardAsync(
        World world, Guid submissionId, int expectedRevision, Guid? actor = null)
    {
        await using var scope = Scope();
        var handler = new DiscardListingDraftCommandHandler(
            new FakeCurrentUser(actor ?? world.Owner),
            scope.Get<IUserRepository>(),
            scope.Get<IListingSubmissionWriteRepository>(),
            scope.Get<IListingSubmissionAuditRecorder>(),
            new FakeClock());

        return await handler.Handle(new DiscardListingDraftCommand(submissionId, expectedRevision));
    }

    private async Task<Result<SubmitListingDraftResult>> SubmitAsync(
        World world, Guid submissionId, int expectedRevision, FakeEvidence evidence)
    {
        await using var scope = Scope();
        var handler = new SubmitListingDraftCommandHandler(
            new FakeCurrentUser(world.Owner),
            scope.Get<IUserRepository>(),
            scope.Get<IListingSubmissionWriteRepository>(),
            evidence,
            new FakeClock());

        return await handler.Handle(new SubmitListingDraftCommand(submissionId, expectedRevision));
    }

    private async Task<int> CountAsync(string table)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table};", connection);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<SubmissionRow> ReadSubmissionAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status, property_id, submitted_at FROM marketplace.submissions WHERE id = @id;", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return new SubmissionRow(
            reader.GetString(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
    }

    private async Task<ListingRow> ReadListingAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT publication_status, published_submission_id FROM marketplace.listings WHERE id = @id;", connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return new ListingRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }

    // Tries a lock from a second connection without waiting; returns the SQLSTATE when it cannot be taken.
    private async Task<string?> TryLockAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Host.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(sql, connection, transaction);

        try
        {
            await command.ExecuteScalarAsync();

            return null;
        }
        catch (PostgresException exception)
        {
            return exception.SqlState;
        }
    }

    private sealed class ScopeHandle(IServiceScope scope) : IAsyncDisposable
    {
        public T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

        public ValueTask DisposeAsync()
        {
            if (scope is IAsyncDisposable asyncDisposable)
            {
                return asyncDisposable.DisposeAsync();
            }

            scope.Dispose();

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;

        public bool IsAuthenticated => true;
    }

    private sealed class FakeClock : IDateTimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    /// <summary>Test-only provider: resolves and cleans whatever it is asked about while available.</summary>
    private sealed class FakeEvidence : IEvidenceAssessmentProvider
    {
        public bool Available { get; set; } = true;

        public int ResolveCalls { get; private set; }

        public Task<EvidenceResolution> ResolveAsync(
            Guid actingUserId, Guid propertyId, ListingPurpose purpose, IReadOnlyCollection<Guid> evidenceIds,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;

            return Task.FromResult(Available
                ? EvidenceResolution.Resolved(evidenceIds
                    .Select(id => new ListingEvidenceReference(id, Guid.NewGuid(), propertyId, purpose, new string('A', 64)))
                    .ToArray())
                : EvidenceResolution.Unavailable());
        }

        public Task<EvidenceAssessmentResult> AssessAsync(
            IReadOnlyCollection<ListingEvidenceReference> evidence, DateTimeOffset at,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Available
                ? EvidenceAssessmentResult.Assessed(evidence
                    .Select(reference => new ListingEvidenceAssessment(Guid.NewGuid(), reference, EvidenceScanStatus.Clean, at))
                    .ToArray())
                : EvidenceAssessmentResult.Unavailable());
        }
    }
}
