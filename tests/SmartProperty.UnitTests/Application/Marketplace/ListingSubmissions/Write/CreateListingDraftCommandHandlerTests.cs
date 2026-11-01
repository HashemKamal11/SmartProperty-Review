using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Create;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.UnitTests.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// Creating a draft: who may, what it is built from, which slot it takes, and that a refused request changes nothing.
/// </summary>
public sealed class CreateListingDraftCommandHandlerTests
{
    private static readonly Guid PropertyId = WriteHandlerWorld.PropertyId;

    private readonly WriteHandlerWorld _world = new();

    [Fact]
    public async Task AnEligibleUserCreatesAnEvidenceFreeFirstDraftBuiltFromTheRegistryAndTheGrant()
    {
        var result = await Handler(_world).Handle(Command());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.Version);
        Assert.Equal(1, result.Value.Revision);
        var draft = Assert.Single(_world.Repository.Submissions);
        Assert.Equal(result.Value.SubmissionId, draft.Id);
        Assert.Equal(result.Value.ListingId, draft.ListingId);
        Assert.Equal(ListingSubmissionStatus.Draft, draft.Status);
        Assert.Empty(draft.Evidence);
        Assert.Equal("Riyadh", draft.Content.City);
        Assert.Equal("Al Olaya", draft.Content.District);
        Assert.Equal(WriteHandlerWorld.OwnerGrant().LegalPartyReferenceId, draft.Authority.DeclaredOwner.LegalPartyReferenceId);
        Assert.Equal(WriteHandlerWorld.UserId, draft.Authority.ActingUserId);
        Assert.NotNull(_world.Repository.Listing);
        Assert.Equal(ListingPublicationStatus.Unpublished, _world.Repository.Listing!.PublicationStatus);
        Assert.Equal(1, _world.Repository.SaveCount);
        Assert.Equal(0, _world.Evidence.ResolveCount);
    }

    [Fact]
    public async Task ARentalCarriesItsRentPeriod()
    {
        var result = await Handler(_world).Handle(
            new CreateListingDraftCommand(PropertyId, ListingPurpose.Rent, WriteHandlerWorld.Input(rentPeriod: RentPeriod.Monthly)));

        Assert.True(result.IsSuccess);
        Assert.Equal(RentPeriod.Monthly, Assert.Single(_world.Repository.Submissions).Content.RentPeriod);
    }

    [Fact]
    public async Task TheLocksAreTakenInThePropertyGrantListingOrderBeforeAnythingIsAdded()
    {
        await Handler(_world).Handle(Command());

        Assert.Equal(
            new[] { "LockProperty", "GetGrant", "GetListing", "AddListing", "AddSubmission", "Save" },
            _world.Repository.Calls);
    }

    [Fact]
    public async Task TheGrantIsReadForTheAuthenticatedUserOnly()
    {
        var other = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var world = new WriteHandlerWorld(acting: other);

        var result = await Handler(world).Handle(Command());

        // The only grant that exists names someone else, so the caller is not eligible however the request is phrased.
        Assert.Same(WriteErrors.NotEligible, result.Error);
        Assert.Equal(other, world.Repository.LastGrantee);
        AssertNothingChanged(world);
    }

    [Fact]
    public async Task AnAnonymousCallerIsA401AndNothingIsRead()
    {
        var world = new WriteHandlerWorld(anonymous: true);

        var result = await Handler(world).Handle(Command());

        Assert.Same(ListingSubmissionErrors.Unauthorized, result.Error);
        Assert.Empty(world.Repository.Calls);
        Assert.Equal(0, world.Evidence.ResolveCount);
    }

    [Fact]
    public async Task AMissingAccountIsA401AndNothingIsRead()
    {
        var world = new WriteHandlerWorld(accountStatus: null);

        var result = await Handler(world).Handle(Command());

        Assert.Same(ListingSubmissionErrors.Unauthorized, result.Error);
        Assert.Empty(world.Repository.Calls);
    }

    [Fact]
    public async Task EveryNonActiveAccountIsTheOneAccountUnavailableErrorAndNothingIsRead()
    {
        foreach (var status in new[] { UserStatus.Pending, UserStatus.Suspended, UserStatus.Deactivated })
        {
            var world = new WriteHandlerWorld(accountStatus: status);

            var result = await Handler(world).Handle(Command());

            Assert.Same(MeErrors.AccountUnavailable, result.Error);
            Assert.Empty(world.Repository.Calls);
            AssertNothingChanged(world);
        }
    }

    [Fact]
    public async Task AnInvalidRequestIsRefusedBeforeAnyProviderOrRepositoryIsAsked()
    {
        var result = await Handler(_world).Handle(
            new CreateListingDraftCommand(PropertyId, ListingPurpose.Sale, WriteHandlerWorld.Input(title: " ")));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Empty(_world.Repository.Calls);
        Assert.Equal(0, _world.Evidence.ResolveCount);
    }

    [Fact]
    public async Task EveryUnsuitableGrantIsTheSameNotEligibleAndNothingChanges()
    {
        var unsuitable = new Func<AuthorityGrantView?>[]
        {
            () => null,
            () => WriteHandlerWorld.OwnerGrant() with { State = AuthorityGrantState.Revoked, RevokedAt = WriteHandlerWorld.Now.AddDays(-1) },
            () => WriteHandlerWorld.OwnerGrant() with { AllowsSale = false },
            () => WriteHandlerWorld.OwnerGrant() with { PropertyId = Guid.NewGuid() },
            () => WriteHandlerWorld.OwnerGrant() with { ValidUntil = WriteHandlerWorld.Now },
            () => WriteHandlerWorld.OwnerGrant() with { ValidFrom = WriteHandlerWorld.Now.AddDays(1) },
            () => WriteHandlerWorld.OwnerGrant() with { IssuedByUserId = WriteHandlerWorld.UserId },
        };

        foreach (var grant in unsuitable)
        {
            var world = new WriteHandlerWorld();
            world.Repository.Grant = grant();

            var result = await Handler(world).Handle(Command());

            Assert.Same(WriteErrors.NotEligible, result.Error);
            AssertNothingChanged(world);
            Assert.Empty(world.Repository.Submissions);
            Assert.Null(world.Repository.Listing);
        }
    }

    [Fact]
    public async Task AGrantForAnotherPurposeDoesNotAllowThisOne()
    {
        _world.Repository.Grant = WriteHandlerWorld.OwnerGrant() with { AllowsSale = true, AllowsRent = false };

        var result = await Handler(_world).Handle(
            new CreateListingDraftCommand(PropertyId, ListingPurpose.Rent, WriteHandlerWorld.Input(rentPeriod: RentPeriod.Monthly)));

        Assert.Same(WriteErrors.NotEligible, result.Error);
        AssertNothingChanged(_world);
    }

    [Fact]
    public async Task AnUnknownPropertyIsTheSameNotEligible()
    {
        _world.Repository.Property = null;

        var result = await Handler(_world).Handle(Command());

        Assert.Same(WriteErrors.NotEligible, result.Error);
        AssertNothingChanged(_world);
    }

    [Fact]
    public async Task AnEligibleUserIsToldOnlyThatAnArchivedOrReservedPropertyCannotBeListed()
    {
        foreach (var property in new[]
        {
            WriteHandlerWorld.Property() with { IsActive = false },
            WriteHandlerWorld.Property() with { HasMarketplaceReservation = true },
            WriteHandlerWorld.Property() with { District = null },
        })
        {
            var world = new WriteHandlerWorld();
            world.Repository.Property = property;

            var result = await Handler(world).Handle(Command());

            Assert.Same(WriteErrors.PropertyUnavailable, result.Error);
            AssertNothingChanged(world);
        }
    }

    [Fact]
    public async Task NamedEvidenceIsResolvedForTheAuthenticatedUserAndStoredAsExactReferences()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var result = await Handler(_world).Handle(new CreateListingDraftCommand(PropertyId, ListingPurpose.Sale, WriteHandlerWorld.Input(ids)));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _world.Evidence.ResolveCount);
        Assert.Equal(WriteHandlerWorld.UserId, _world.Evidence.ResolvedFor);
        Assert.Equal(ids, _world.Evidence.ResolvedIds!.ToArray());
        var draft = Assert.Single(_world.Repository.Submissions);
        Assert.Equal(ids, draft.Evidence.Select(reference => reference.EvidenceId).ToArray());
        Assert.Equal(0, _world.Evidence.AssessCount);
    }

    [Fact]
    public async Task AnUnavailableEvidenceProviderIsTheGenericUnverifiedConflictAndNothingIsLocked()
    {
        _world.Evidence.OnResolve = _ => EvidenceResolution.Unavailable();

        var result = await Handler(_world).Handle(
            new CreateListingDraftCommand(PropertyId, ListingPurpose.Sale, WriteHandlerWorld.Input([Guid.NewGuid()])));

        Assert.Same(WriteErrors.PrerequisitesUnverified, result.Error);
        Assert.Empty(_world.Repository.Calls);
        AssertNothingChanged(_world);
    }

    [Fact]
    public async Task EvidenceTheProviderDoesNotHoldIsAGenericValidationFailure()
    {
        _world.Evidence.OnResolve = _ => EvidenceResolution.Resolved([]);

        var result = await Handler(_world).Handle(
            new CreateListingDraftCommand(PropertyId, ListingPurpose.Sale, WriteHandlerWorld.Input([Guid.NewGuid()])));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Empty(_world.Repository.Calls);
    }

    [Fact]
    public async Task EvidenceForAnotherPropertyIsNotAccepted()
    {
        var id = Guid.NewGuid();
        _world.Evidence.OnResolve = _ => EvidenceResolution.Resolved([WriteHandlerWorld.Reference(id, propertyId: Guid.NewGuid())]);

        var result = await Handler(_world).Handle(
            new CreateListingDraftCommand(PropertyId, ListingPurpose.Sale, WriteHandlerWorld.Input([id])));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        AssertNothingChanged(_world);
    }

    [Fact]
    public async Task AnOpenDraftOrAVersionAwaitingReviewTakesTheSlotAndNothingIsAdded()
    {
        _world.SeedDraft();

        var openDraft = await Handler(_world).Handle(Command());

        Assert.Same(WriteErrors.OpenRevisionExists, openDraft.Error);
        Assert.Equal(0, _world.Repository.SaveCount);
        Assert.Equal(0, _world.Repository.PendingChangeCount);
        Assert.Single(_world.Repository.Submissions);

        var world = new WriteHandlerWorld();
        var listing = MarketplaceFixtures.CreateListing(ListingPurpose.Sale, PropertyId, publish: false);
        world.Repository.SeedListing(listing);
        world.Repository.SeedSubmission(MarketplaceFixtures.Pending(listing, MarketplaceFixtures.PublishedAt));

        var pending = await Handler(world).Handle(Command());

        Assert.Same(WriteErrors.OpenRevisionExists, pending.Error);
        AssertNothingChanged(world);
    }

    [Fact]
    public async Task AReviewedVersionAllowsASuccessorAndThePublishedVersionIsPreserved()
    {
        var (listing, published) = _world.SeedPublished();

        var result = await Handler(_world).Handle(Command());

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Version);
        Assert.Equal(listing.Id, result.Value.ListingId);
        var draft = _world.Repository.Submissions.Single(submission => submission.Id == result.Value.SubmissionId);
        Assert.Equal(published.Id, draft.PredecessorId);
        Assert.Equal(ListingSubmissionStatus.Approved, published.Status);
        Assert.Equal(ListingPublicationStatus.Published, listing.PublicationStatus);
        Assert.Equal(published.Id, listing.PublishedSubmissionId);
        Assert.DoesNotContain("AddListing", _world.Repository.Calls);
    }

    [Fact]
    public async Task ALostRaceOnTheSlotOrTheVersionIsTheOpenRevisionConflictAndNothingIsCommitted()
    {
        foreach (var kind in new[] { ListingWriteConflictKind.ListingSlot, ListingWriteConflictKind.SubmissionVersion })
        {
            var world = new WriteHandlerWorld();
            world.Repository.ExceptionOnSave = new ListingWriteConflictException(kind);

            var result = await Handler(world).Handle(Command());

            Assert.Same(WriteErrors.OpenRevisionExists, result.Error);
            Assert.Empty(world.Repository.Submissions);
            Assert.Null(world.Repository.Listing);
        }
    }

    [Fact]
    public async Task AStaleRevisionConflictIsNotACreateConflictAndPropagates()
    {
        _world.Repository.ExceptionOnSave = new ListingWriteConflictException(ListingWriteConflictKind.StaleRevision);

        await Assert.ThrowsAsync<ListingWriteConflictException>(() => Handler(_world).Handle(Command()));
    }

    [Fact]
    public async Task InfrastructureFailuresPropagateAndAreNeverUserErrors()
    {
        _world.Repository.ExceptionOnSave = new InvalidOperationException("database unavailable");
        var onSave = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(_world).Handle(Command()));
        Assert.Equal("database unavailable", onSave.Message);

        var onRead = new WriteHandlerWorld();
        onRead.Repository.ExceptionOnRead = new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(() => Handler(onRead).Handle(Command()));

        var onEvidence = new WriteHandlerWorld();
        onEvidence.Evidence.ExceptionToThrow = new HttpRequestException();
        await Assert.ThrowsAsync<HttpRequestException>(
            () => Handler(onEvidence).Handle(new CreateListingDraftCommand(PropertyId, ListingPurpose.Sale, WriteHandlerWorld.Input([Guid.NewGuid()]))));
    }

    [Fact]
    public async Task CancellationReachesTheRepositoryAndIsNeverSwallowed()
    {
        using var source = new CancellationTokenSource();

        await Handler(_world).Handle(Command(), source.Token);

        Assert.Equal(source.Token, _world.Repository.LastToken);

        var cancelled = new WriteHandlerWorld();
        cancelled.Evidence.ExceptionToThrow = new OperationCanceledException(source.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Handler(cancelled).Handle(
                new CreateListingDraftCommand(PropertyId, ListingPurpose.Sale, WriteHandlerWorld.Input([Guid.NewGuid()])),
                source.Token));
    }

    [Fact]
    public async Task TheCommandOffersNoWayToSupplyAuthorityIdentityOrVerification()
    {
        await Task.CompletedTask;

        Assert.Equal(
            new[] { "PropertyId", "Purpose", "Content" },
            typeof(CreateListingDraftCommand).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name!).ToArray());
    }

    private static CreateListingDraftCommandHandler Handler(WriteHandlerWorld world)
    {
        return new CreateListingDraftCommandHandler(world.CurrentUser, world.Users, world.Repository, world.Evidence, world.Clock);
    }

    private static CreateListingDraftCommand Command()
    {
        return new CreateListingDraftCommand(PropertyId, ListingPurpose.Sale, WriteHandlerWorld.Input());
    }

    private static void AssertNothingChanged(WriteHandlerWorld world)
    {
        Assert.Equal(0, world.Repository.SaveCount);
        Assert.Equal(0, world.Repository.PendingChangeCount);
        Assert.DoesNotContain("AddListing", world.Repository.Calls);
        Assert.DoesNotContain("AddSubmission", world.Repository.Calls);
    }
}
