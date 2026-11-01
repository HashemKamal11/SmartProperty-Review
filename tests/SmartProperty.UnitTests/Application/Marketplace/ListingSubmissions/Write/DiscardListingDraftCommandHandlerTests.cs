using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Discard;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// Discarding a draft: the draft, its listing when nothing else needs it, and the audit event leave together in one
/// commit or not at all, and nothing that was submitted or published is ever touched.
/// </summary>
public sealed class DiscardListingDraftCommandHandlerTests
{
    private readonly WriteHandlerWorld _world = new();

    [Fact]
    public async Task TheOnlyDraftOfAListingIsRemovedWithItsListingAndAuditedInOneCommit()
    {
        var draft = _world.SeedDraft();
        var listingId = draft.ListingId;

        var result = await Handler(_world).Handle(new DiscardListingDraftCommand(draft.Id, draft.Revision));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.ListingRemoved);
        Assert.Empty(_world.Repository.Submissions);
        Assert.Null(_world.Repository.Listing);
        var audit = Assert.Single(_world.Repository.CommittedAudit);
        Assert.Equal(draft.Id, audit.SubmissionId);
        Assert.Equal(listingId, audit.ListingId);
        Assert.Equal(WriteHandlerWorld.PropertyId, audit.PropertyId);
        Assert.Equal(ListingPurpose.Sale, audit.Purpose);
        Assert.Equal(1, audit.Version);
        Assert.Equal(WriteHandlerWorld.UserId, audit.ActorUserId);
        Assert.Equal(WriteHandlerWorld.Now, audit.OccurredAt);
        Assert.True(audit.ListingDeleted);
        Assert.NotEqual(Guid.Empty, audit.EventId);
    }

    [Fact]
    public async Task EverythingIsTrackedFirstAndCommittedByExactlyOneSave()
    {
        var draft = _world.SeedDraft();

        await Handler(_world).Handle(new DiscardListingDraftCommand(draft.Id, draft.Revision));

        Assert.Equal(
            new[] { "GetOwn", "LockProperty", "GetGrant", "GetListing", "GetOwn", "CanRemoveListing", "RemoveDraft", "RemoveListing", "Audit", "Save" },
            _world.Repository.Calls);
        Assert.Equal(1, _world.Repository.SaveCount);
        Assert.Equal(1, _world.Audit.CallCount);
    }

    [Fact]
    public async Task ADraftWithAPublishedPredecessorIsRemovedAloneAndTheListingStaysPublished()
    {
        var (listing, published) = _world.SeedPublished();
        var draft = _world.SeedDraftIn(listing, evidenceCount: 0, predecessor: published);

        var result = await Handler(_world).Handle(new DiscardListingDraftCommand(draft.Id, draft.Revision));

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.ListingRemoved);
        Assert.Same(published, Assert.Single(_world.Repository.Submissions));
        Assert.Same(listing, _world.Repository.Listing);
        Assert.Equal(ListingPublicationStatus.Published, listing.PublicationStatus);
        Assert.Equal(published.Id, listing.PublishedSubmissionId);
        Assert.Equal(ListingSubmissionStatus.Approved, published.Status);
        Assert.False(Assert.Single(_world.Repository.CommittedAudit).ListingDeleted);
        Assert.DoesNotContain("RemoveListing", _world.Repository.Calls);
    }

    [Fact]
    public async Task AFailedCommitLeavesTheDraftTheListingAndTheAuditUntouched()
    {
        var draft = _world.SeedDraft();
        _world.Repository.ExceptionOnSave = new InvalidOperationException("database unavailable");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Handler(_world).Handle(new DiscardListingDraftCommand(draft.Id, draft.Revision)));

        Assert.Same(draft, Assert.Single(_world.Repository.Submissions));
        Assert.NotNull(_world.Repository.Listing);
        Assert.Empty(_world.Repository.CommittedAudit);
        Assert.Equal(0, _world.Repository.PendingChangeCount);
    }

    [Fact]
    public async Task AnAuditRecorderThatFailsPreventsTheCommitAndAnythingBeingRemoved()
    {
        var draft = _world.SeedDraft();
        _world.Audit.ExceptionToThrow = new InvalidOperationException("audit store unavailable");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Handler(_world).Handle(new DiscardListingDraftCommand(draft.Id, draft.Revision)));

        Assert.Equal(0, _world.Repository.SaveCount);
        Assert.Same(draft, Assert.Single(_world.Repository.Submissions));
        Assert.NotNull(_world.Repository.Listing);
        Assert.Empty(_world.Repository.CommittedAudit);
    }

    [Fact]
    public async Task ALostRaceOnTheRevisionIsStaleAndNothingIsCommitted()
    {
        var draft = _world.SeedDraft();
        _world.Repository.ExceptionOnSave = new ListingWriteConflictException(ListingWriteConflictKind.StaleRevision);

        var result = await Handler(_world).Handle(new DiscardListingDraftCommand(draft.Id, draft.Revision));

        Assert.Same(WriteErrors.StaleRevision, result.Error);
        Assert.Same(draft, Assert.Single(_world.Repository.Submissions));
        Assert.Empty(_world.Repository.CommittedAudit);

        var other = new WriteHandlerWorld();
        var otherDraft = other.SeedDraft();
        other.Repository.ExceptionOnSave = new ListingWriteConflictException(ListingWriteConflictKind.SubmissionVersion);
        await Assert.ThrowsAsync<ListingWriteConflictException>(
            () => Handler(other).Handle(new DiscardListingDraftCommand(otherDraft.Id, otherDraft.Revision)));
    }

    [Fact]
    public async Task AStaleRevisionIsRefusedAndNothingIsTracked()
    {
        var draft = _world.SeedDraft();

        foreach (var expected in new[] { 0, 2, 99 })
        {
            var result = await Handler(_world).Handle(new DiscardListingDraftCommand(draft.Id, expected));

            Assert.Same(WriteErrors.StaleRevision, result.Error);
        }

        Assert.DoesNotContain("RemoveDraft", _world.Repository.Calls);
        Assert.Equal(0, _world.Audit.CallCount);
        Assert.Equal(0, _world.Repository.SaveCount);
        Assert.Same(draft, Assert.Single(_world.Repository.Submissions));
    }

    [Fact]
    public async Task ASubmittedVersionIsNeverDiscardedEvenAtItsCurrentRevision()
    {
        var draft = _world.SeedDraft(evidenceCount: 1);
        draft.Submit(draft.Revision, WriteHandlerWorld.UserId, null, WriteHandlerWorld.Now, draft.Evidence.Select(reference => WriteHandlerWorld.Assessment(reference)).ToArray(), true, null);

        var result = await Handler(_world).Handle(new DiscardListingDraftCommand(draft.Id, draft.Revision));

        Assert.Same(WriteErrors.StaleRevision, result.Error);
        Assert.DoesNotContain("RemoveDraft", _world.Repository.Calls);
        Assert.Equal(0, _world.Repository.SaveCount);
        Assert.Same(draft, Assert.Single(_world.Repository.Submissions));
        Assert.Equal(ListingSubmissionStatus.PendingReview, draft.Status);
    }

    [Fact]
    public async Task AMissingDraftAndSomeoneElsesDraftAreTheSameNotFound()
    {
        var other = new WriteHandlerWorld(acting: Guid.Parse("99999999-9999-9999-9999-999999999999"));
        var othersDraft = other.SeedDraft();

        var missing = await Handler(other).Handle(new DiscardListingDraftCommand(Guid.NewGuid(), 1));
        var notMine = await Handler(other).Handle(new DiscardListingDraftCommand(othersDraft.Id, 1));

        Assert.Same(ListingSubmissionErrors.NotFound, missing.Error);
        Assert.Same(ListingSubmissionErrors.NotFound, notMine.Error);
        Assert.Same(othersDraft, Assert.Single(other.Repository.Submissions));
        Assert.Equal(0, other.Audit.CallCount);
    }

    [Fact]
    public async Task AnonymousAndInactiveAccountsAreRefusedBeforeAnythingIsRead()
    {
        var anonymous = new WriteHandlerWorld(anonymous: true);
        var suspended = new WriteHandlerWorld(accountStatus: UserStatus.Suspended);
        var id = Guid.NewGuid();

        var anonymousResult = await Handler(anonymous).Handle(new DiscardListingDraftCommand(id, 1));
        var suspendedResult = await Handler(suspended).Handle(new DiscardListingDraftCommand(id, 1));

        Assert.Same(ListingSubmissionErrors.Unauthorized, anonymousResult.Error);
        Assert.Same(MeErrors.AccountUnavailable, suspendedResult.Error);
        Assert.Empty(anonymous.Repository.Calls);
        Assert.Empty(suspended.Repository.Calls);
    }

    [Fact]
    public async Task VerifiedAuthorityIsRequiredAndARevokedGrantStopsTheDiscard()
    {
        var draft = _world.SeedDraft();
        _world.Repository.Grant = WriteHandlerWorld.OwnerGrant() with { State = AuthorityGrantState.Revoked, RevokedAt = WriteHandlerWorld.Now.AddDays(-1) };

        var result = await Handler(_world).Handle(new DiscardListingDraftCommand(draft.Id, draft.Revision));

        Assert.Same(WriteErrors.NotEligible, result.Error);
        Assert.Same(draft, Assert.Single(_world.Repository.Submissions));
        Assert.Equal(0, _world.Audit.CallCount);
    }

    [Fact]
    public async Task AnEligibleUserCanStillDiscardOnceThePropertyIsArchivedOrReserved()
    {
        foreach (var property in new[]
        {
            WriteHandlerWorld.Property() with { IsActive = false },
            WriteHandlerWorld.Property() with { HasMarketplaceReservation = true },
        })
        {
            var world = new WriteHandlerWorld();
            var draft = world.SeedDraft();
            world.Repository.Property = property;

            var result = await Handler(world).Handle(new DiscardListingDraftCommand(draft.Id, draft.Revision));

            Assert.True(result.IsSuccess);
            Assert.Empty(world.Repository.Submissions);
        }
    }

    [Fact]
    public async Task AnIneligibleUserLearnsNothingAboutAnArchivedProperty()
    {
        var draft = _world.SeedDraft();
        _world.Repository.Property = WriteHandlerWorld.Property() with { IsActive = false };
        _world.Repository.Grant = null;

        var result = await Handler(_world).Handle(new DiscardListingDraftCommand(draft.Id, draft.Revision));

        Assert.Same(WriteErrors.NotEligible, result.Error);
    }

    [Fact]
    public async Task ThereIsNoDiscardedStatusAndNoSoftDelete()
    {
        await Task.CompletedTask;

        Assert.DoesNotContain(Enum.GetNames<ListingSubmissionStatus>(), name => name.Contains("Discard", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "SubmissionId", "ExpectedRevision" }, typeof(DiscardListingDraftCommand).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name!).ToArray());
    }

    [Fact]
    public async Task InfrastructureFailureAndCancellationPropagate()
    {
        using var source = new CancellationTokenSource();
        var draft = _world.SeedDraft();
        _world.Repository.ExceptionOnRead = new OperationCanceledException(source.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Handler(_world).Handle(new DiscardListingDraftCommand(draft.Id, draft.Revision), source.Token));

        Assert.Same(draft, Assert.Single(_world.Repository.Submissions));

        var token = new WriteHandlerWorld();
        var tokenDraft = token.SeedDraft();
        await Handler(token).Handle(new DiscardListingDraftCommand(tokenDraft.Id, tokenDraft.Revision), source.Token);
        Assert.Equal(source.Token, token.Repository.LastToken);
    }

    [Fact]
    public async Task AnInvalidRequestIsRefusedBeforeAnyRead()
    {
        var result = await Handler(_world).Handle(new DiscardListingDraftCommand(Guid.Empty, 1));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Empty(_world.Repository.Calls);
    }

    private static DiscardListingDraftCommandHandler Handler(WriteHandlerWorld world)
    {
        return new DiscardListingDraftCommandHandler(world.CurrentUser, world.Users, world.Repository, world.Audit, world.Clock);
    }
}
