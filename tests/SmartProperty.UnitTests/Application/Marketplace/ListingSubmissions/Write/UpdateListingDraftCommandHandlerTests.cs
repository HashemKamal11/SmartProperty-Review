using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Update;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// Updating a draft: only the owner's never-submitted draft, only at the revision the client saw, only while the grant
/// holds, and never touching what was submitted.
/// </summary>
public sealed class UpdateListingDraftCommandHandlerTests
{
    private readonly WriteHandlerWorld _world = new();

    [Fact]
    public async Task TheOwnerReplacesTheContentAndTheDomainIncrementsTheRevision()
    {
        var draft = _world.SeedDraft();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        var result = await Handler(_world).Handle(
            new UpdateListingDraftCommand(draft.Id, draft.Revision, WriteHandlerWorld.Input(ids, title: "A better title")));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Revision);
        Assert.Equal(draft.Id, result.Value.SubmissionId);
        Assert.Equal(draft.ListingId, result.Value.ListingId);
        Assert.Equal(1, result.Value.Version);
        Assert.Equal("A better title", draft.Content.Title);
        Assert.Equal(ids, draft.Evidence.Select(reference => reference.EvidenceId).ToArray());
        Assert.Equal(ListingSubmissionStatus.Draft, draft.Status);
        Assert.Equal(1, _world.Repository.SaveCount);
        Assert.Equal(1, _world.Evidence.ResolveCount);
        Assert.Equal(WriteHandlerWorld.UserId, _world.Evidence.ResolvedFor);
    }

    [Fact]
    public async Task AnUpdateMayRemoveAllEvidenceFromADraft()
    {
        var draft = _world.SeedDraft(evidenceCount: 2);

        var result = await Handler(_world).Handle(new UpdateListingDraftCommand(draft.Id, draft.Revision, WriteHandlerWorld.Input()));

        Assert.True(result.IsSuccess);
        Assert.Empty(draft.Evidence);
        Assert.Equal(0, _world.Evidence.ResolveCount);
    }

    [Fact]
    public async Task LocationAndTypeAreRebuiltFromTheRegistryAndThePurposeNeverChanges()
    {
        var draft = _world.SeedDraft();
        _world.Repository.Property = WriteHandlerWorld.Property() with { City = "Jeddah", District = "Al Hamra" };

        var result = await Handler(_world).Handle(new UpdateListingDraftCommand(draft.Id, draft.Revision, WriteHandlerWorld.Input()));

        Assert.True(result.IsSuccess);
        Assert.Equal("Jeddah", draft.Content.City);
        Assert.Equal("Al Hamra", draft.Content.District);
        Assert.Equal(ListingPurpose.Sale, draft.Content.Purpose);
        Assert.Equal(ListingPurpose.Sale, draft.Purpose);
    }

    [Fact]
    public async Task TheAuthorityOfTheDraftIsNotTouched()
    {
        var draft = _world.SeedDraft();
        var before = draft.Authority;

        await Handler(_world).Handle(new UpdateListingDraftCommand(draft.Id, draft.Revision, WriteHandlerWorld.Input()));

        Assert.Same(before, draft.Authority);
    }

    [Fact]
    public async Task TheLocksAreTakenInOrderAndTheDraftIsReadAgainUnderThem()
    {
        var draft = _world.SeedDraft();

        await Handler(_world).Handle(new UpdateListingDraftCommand(draft.Id, draft.Revision, WriteHandlerWorld.Input()));

        Assert.Equal(
            new[] { "GetOwn", "LockProperty", "GetGrant", "GetListing", "GetOwn", "Save" },
            _world.Repository.Calls);
    }

    [Fact]
    public async Task AStaleRevisionIsRefusedAndTheDraftIsNeverOverwritten()
    {
        var draft = _world.SeedDraft();
        var title = draft.Content.Title;

        foreach (var expected in new[] { 0, draft.Revision + 1, 99 })
        {
            var result = await Handler(_world).Handle(new UpdateListingDraftCommand(draft.Id, expected, WriteHandlerWorld.Input(title: "OVERWRITE")));

            Assert.Same(WriteErrors.StaleRevision, result.Error);
        }

        Assert.Equal(title, draft.Content.Title);
        Assert.Equal(1, draft.Revision);
        Assert.Equal(0, _world.Repository.SaveCount);
    }

    [Fact]
    public async Task ASubmittedDraftIsImmutableEvenAtItsCurrentRevision()
    {
        var draft = _world.SeedDraft(evidenceCount: 1);
        draft.Submit(draft.Revision, WriteHandlerWorld.UserId, null, WriteHandlerWorld.Now, draft.Evidence.Select(reference => WriteHandlerWorld.Assessment(reference)).ToArray(), true, null);
        var revision = draft.Revision;
        var title = draft.Content.Title;

        var result = await Handler(_world).Handle(new UpdateListingDraftCommand(draft.Id, revision, WriteHandlerWorld.Input(title: "OVERWRITE")));

        Assert.Same(WriteErrors.StaleRevision, result.Error);
        Assert.Equal(title, draft.Content.Title);
        Assert.Equal(revision, draft.Revision);
        Assert.Equal(ListingSubmissionStatus.PendingReview, draft.Status);
        Assert.Equal(0, _world.Repository.SaveCount);
    }

    [Fact]
    public async Task AMissingDraftAndSomeoneElsesDraftAreTheSameNotFound()
    {
        var draft = _world.SeedDraft();
        var other = new WriteHandlerWorld(acting: Guid.Parse("99999999-9999-9999-9999-999999999999"));
        var othersDraft = other.SeedDraft();

        var missing = await Handler(other).Handle(new UpdateListingDraftCommand(Guid.NewGuid(), 1, WriteHandlerWorld.Input()));
        var foreign = await Handler(_world).Handle(new UpdateListingDraftCommand(Guid.NewGuid(), draft.Revision, WriteHandlerWorld.Input()));
        var notMine = await Handler(other).Handle(new UpdateListingDraftCommand(othersDraft.Id, 1, WriteHandlerWorld.Input()));

        foreach (var result in new[] { missing, foreign, notMine })
        {
            Assert.Same(ListingSubmissionErrors.NotFound, result.Error);
        }

        Assert.Equal(1, othersDraft.Revision);
        Assert.Equal(0, other.Repository.SaveCount);
    }

    [Fact]
    public async Task AnonymousAndInactiveAccountsAreRefusedBeforeAnythingIsRead()
    {
        var anonymous = new WriteHandlerWorld(anonymous: true);
        var missing = new WriteHandlerWorld(accountStatus: null);
        var suspended = new WriteHandlerWorld(accountStatus: UserStatus.Suspended);
        var id = Guid.NewGuid();

        var anonymousResult = await Handler(anonymous).Handle(new UpdateListingDraftCommand(id, 1, WriteHandlerWorld.Input()));
        var missingResult = await Handler(missing).Handle(new UpdateListingDraftCommand(id, 1, WriteHandlerWorld.Input()));
        var suspendedResult = await Handler(suspended).Handle(new UpdateListingDraftCommand(id, 1, WriteHandlerWorld.Input()));

        Assert.Same(ListingSubmissionErrors.Unauthorized, anonymousResult.Error);
        Assert.Same(ListingSubmissionErrors.Unauthorized, missingResult.Error);
        Assert.Same(MeErrors.AccountUnavailable, suspendedResult.Error);
        Assert.All(new[] { anonymous, missing, suspended }, world => Assert.Empty(world.Repository.Calls));
    }

    [Fact]
    public async Task ARevokedOrExpiredGrantMeansTheDraftCanNoLongerBeEdited()
    {
        foreach (var grant in new AuthorityGrantView?[]
        {
            null,
            WriteHandlerWorld.OwnerGrant() with { State = AuthorityGrantState.Revoked, RevokedAt = WriteHandlerWorld.Now.AddDays(-1) },
            WriteHandlerWorld.OwnerGrant() with { ValidUntil = WriteHandlerWorld.Now },
        })
        {
            var world = new WriteHandlerWorld();
            var draft = world.SeedDraft();
            world.Repository.Grant = grant;

            var result = await Handler(world).Handle(new UpdateListingDraftCommand(draft.Id, draft.Revision, WriteHandlerWorld.Input(title: "OVERWRITE")));

            Assert.Same(WriteErrors.NotEligible, result.Error);
            Assert.NotEqual("OVERWRITE", draft.Content.Title);
            Assert.Equal(1, draft.Revision);
            Assert.Equal(0, world.Repository.SaveCount);
        }
    }

    [Fact]
    public async Task AnArchivedPropertyCannotBeEditedForAnEligibleUser()
    {
        var draft = _world.SeedDraft();
        _world.Repository.Property = WriteHandlerWorld.Property() with { IsActive = false };

        var result = await Handler(_world).Handle(new UpdateListingDraftCommand(draft.Id, draft.Revision, WriteHandlerWorld.Input()));

        Assert.Same(WriteErrors.PropertyUnavailable, result.Error);
        Assert.Equal(1, draft.Revision);
    }

    [Fact]
    public async Task TheRentPeriodMustMatchTheDraftsOwnPurpose()
    {
        var sale = _world.SeedDraft();
        var saleResult = await Handler(_world).Handle(
            new UpdateListingDraftCommand(sale.Id, sale.Revision, WriteHandlerWorld.Input(rentPeriod: RentPeriod.Monthly)));

        var rentWorld = new WriteHandlerWorld();
        var rent = rentWorld.SeedDraft(purpose: ListingPurpose.Rent);
        var rentResult = await Handler(rentWorld).Handle(new UpdateListingDraftCommand(rent.Id, rent.Revision, WriteHandlerWorld.Input()));

        Assert.Equal(ErrorType.Validation, saleResult.Error!.Type);
        Assert.Equal(ErrorType.Validation, rentResult.Error!.Type);
        Assert.Equal(1, sale.Revision);
        Assert.Equal(1, rent.Revision);
    }

    [Fact]
    public async Task EvidenceProblemsAreRefusedBeforeAnythingIsLocked()
    {
        var draft = _world.SeedDraft();
        var unavailable = new WriteHandlerWorld();
        var unavailableDraft = unavailable.SeedDraft();
        unavailable.Evidence.OnResolve = _ => EvidenceResolution.Unavailable();
        var missing = new WriteHandlerWorld();
        var missingDraft = missing.SeedDraft();
        missing.Evidence.OnResolve = _ => EvidenceResolution.Resolved([]);
        var ids = new[] { Guid.NewGuid() };

        var unavailableResult = await Handler(unavailable).Handle(new UpdateListingDraftCommand(unavailableDraft.Id, 1, WriteHandlerWorld.Input(ids)));
        var missingResult = await Handler(missing).Handle(new UpdateListingDraftCommand(missingDraft.Id, 1, WriteHandlerWorld.Input(ids)));

        Assert.Same(WriteErrors.PrerequisitesUnverified, unavailableResult.Error);
        Assert.Equal(ErrorType.Validation, missingResult.Error!.Type);
        Assert.DoesNotContain("LockProperty", unavailable.Repository.Calls);
        Assert.DoesNotContain("LockProperty", missing.Repository.Calls);
        Assert.Equal(1, unavailableDraft.Revision);
        Assert.Equal(1, draft.Revision);
    }

    [Fact]
    public async Task ALostRaceOnTheRevisionIsStaleAndOtherFailuresPropagate()
    {
        var draft = _world.SeedDraft();
        _world.Repository.ExceptionOnSave = new ListingWriteConflictException(ListingWriteConflictKind.StaleRevision);

        var result = await Handler(_world).Handle(new UpdateListingDraftCommand(draft.Id, draft.Revision, WriteHandlerWorld.Input()));

        Assert.Same(WriteErrors.StaleRevision, result.Error);

        var other = new WriteHandlerWorld();
        var otherDraft = other.SeedDraft();
        other.Repository.ExceptionOnSave = new ListingWriteConflictException(ListingWriteConflictKind.ListingSlot);
        await Assert.ThrowsAsync<ListingWriteConflictException>(
            () => Handler(other).Handle(new UpdateListingDraftCommand(otherDraft.Id, otherDraft.Revision, WriteHandlerWorld.Input())));

        var broken = new WriteHandlerWorld();
        var brokenDraft = broken.SeedDraft();
        broken.Repository.ExceptionOnSave = new InvalidOperationException("database unavailable");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Handler(broken).Handle(new UpdateListingDraftCommand(brokenDraft.Id, brokenDraft.Revision, WriteHandlerWorld.Input())));
    }

    [Fact]
    public async Task ProviderFailureAndCancellationPropagate()
    {
        using var source = new CancellationTokenSource();
        var draft = _world.SeedDraft();
        _world.Evidence.ExceptionToThrow = new OperationCanceledException(source.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Handler(_world).Handle(new UpdateListingDraftCommand(draft.Id, draft.Revision, WriteHandlerWorld.Input([Guid.NewGuid()])), source.Token));

        Assert.Equal(1, draft.Revision);

        var token = new WriteHandlerWorld();
        var tokenDraft = token.SeedDraft();
        await Handler(token).Handle(new UpdateListingDraftCommand(tokenDraft.Id, tokenDraft.Revision, WriteHandlerWorld.Input()), source.Token);
        Assert.Equal(source.Token, token.Repository.LastToken);
    }

    [Fact]
    public async Task AnInvalidRequestIsRefusedBeforeAnyRead()
    {
        var result = await Handler(_world).Handle(new UpdateListingDraftCommand(Guid.Empty, 1, WriteHandlerWorld.Input()));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Empty(_world.Repository.Calls);
    }

    private static UpdateListingDraftCommandHandler Handler(WriteHandlerWorld world)
    {
        return new UpdateListingDraftCommandHandler(world.CurrentUser, world.Users, world.Repository, world.Evidence, world.Clock);
    }
}
