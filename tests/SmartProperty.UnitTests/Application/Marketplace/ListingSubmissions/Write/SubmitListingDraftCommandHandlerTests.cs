using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Authentication.Me;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Application.Marketplace.ListingSubmissions;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write.Submit;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using Xunit;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// Submitting a draft: everything is established under the locks from trusted sources, at least one Clean assessment is
/// required, only the draft changes, and a refused request changes nothing.
/// </summary>
public sealed class SubmitListingDraftCommandHandlerTests
{
    private readonly WriteHandlerWorld _world = new();

    [Fact]
    public async Task AVerifiedDraftWithCleanEvidenceIsSubmittedForReviewAndNotPublished()
    {
        var draft = _world.SeedDraft(evidenceCount: 2);
        var listing = _world.Repository.Listing!;

        var result = await Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision));

        Assert.True(result.IsSuccess);
        Assert.Equal(draft.Id, result.Value.SubmissionId);
        Assert.Equal(ListingSubmissionStatus.PendingReview, result.Value.Status);
        Assert.Equal(WriteHandlerWorld.Now, result.Value.SubmittedAt);
        Assert.Equal(ListingSubmissionStatus.PendingReview, draft.Status);
        Assert.Equal(2, draft.Revision);
        Assert.Equal(2, draft.SubmittedAssessments.Count);
        Assert.Equal(ListingPublicationStatus.Unpublished, listing.PublicationStatus);
        Assert.Null(listing.PublishedSubmissionId);
        Assert.Equal(1, _world.Repository.SaveCount);
        Assert.Equal(1, _world.Evidence.AssessCount);
        Assert.Equal(0, _world.Evidence.ResolveCount);
    }

    [Fact]
    public async Task ThePreviouslyPublishedVersionIsPreservedWhileTheNewOneIsPending()
    {
        var (listing, published) = _world.SeedPublished();
        var draft = _world.SeedDraftIn(listing, evidenceCount: 1, predecessor: published);

        var result = await Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision));

        Assert.True(result.IsSuccess);
        Assert.Equal(ListingSubmissionStatus.PendingReview, draft.Status);
        Assert.Equal(ListingSubmissionStatus.Approved, published.Status);
        Assert.Equal(ListingPublicationStatus.Published, listing.PublicationStatus);
        Assert.Equal(published.Id, listing.PublishedSubmissionId);
        Assert.Equal(2, _world.Repository.Submissions.Count);
    }

    [Fact]
    public async Task TheLocksAreTakenInOrderAndTheDraftIsReadAgainUnderThemBeforeAnythingIsAssessed()
    {
        var draft = _world.SeedDraft(evidenceCount: 1);

        await Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision));

        Assert.Equal(
            new[] { "GetOwn", "LockProperty", "GetGrant", "GetListing", "GetOwn", "Save" },
            _world.Repository.Calls);
    }

    [Fact]
    public async Task ADraftWithNoEvidenceCannotBeSubmittedAndTheProviderIsNotAsked()
    {
        var draft = _world.SeedDraft(evidenceCount: 0);

        var result = await Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision));

        Assert.Same(WriteErrors.PrerequisitesUnverified, result.Error);
        Assert.Equal(0, _world.Evidence.AssessCount);
        AssertUnchanged(_world, draft);
    }

    [Fact]
    public async Task AnUnavailableProviderFailsClosedWithTheGenericUnverifiedConflict()
    {
        var draft = _world.SeedDraft(evidenceCount: 1);
        _world.Evidence.OnAssess = _ => EvidenceAssessmentResult.Unavailable();

        var result = await Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision));

        Assert.Same(WriteErrors.PrerequisitesUnverified, result.Error);
        Assert.Equal("listing_submissions.prerequisites_unverified", result.Error!.Code);
        Assert.Equal(ErrorType.Conflict, result.Error.Type);
        AssertUnchanged(_world, draft);
    }

    [Fact]
    public async Task AnythingOtherThanCleanEvidenceIsTheSameGenericConflict()
    {
        foreach (var status in new[] { EvidenceScanStatus.Unknown, EvidenceScanStatus.Pending, EvidenceScanStatus.Rejected, EvidenceScanStatus.Failed })
        {
            var world = new WriteHandlerWorld();
            var draft = world.SeedDraft(evidenceCount: 2);
            world.Evidence.OnAssess = evidence => EvidenceAssessmentResult.Assessed(
                evidence.Select((reference, index) => WriteHandlerWorld.Assessment(reference, index == 1 ? status : EvidenceScanStatus.Clean)).ToArray());

            var result = await Handler(world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision));

            Assert.Same(WriteErrors.PrerequisitesUnverified, result.Error);
            AssertUnchanged(world, draft);
        }
    }

    [Fact]
    public async Task AMissingOrExtraAssessmentIsTheSameGenericConflict()
    {
        var missing = new WriteHandlerWorld();
        var missingDraft = missing.SeedDraft(evidenceCount: 2);
        missing.Evidence.OnAssess = evidence => EvidenceAssessmentResult.Assessed([WriteHandlerWorld.Assessment(evidence.First())]);

        var extra = new WriteHandlerWorld();
        var extraDraft = extra.SeedDraft(evidenceCount: 1);
        extra.Evidence.OnAssess = evidence => EvidenceAssessmentResult.Assessed(
            [WriteHandlerWorld.Assessment(evidence.First()), WriteHandlerWorld.Assessment(WriteHandlerWorld.Reference())]);

        var missingResult = await Handler(missing).Handle(new SubmitListingDraftCommand(missingDraft.Id, missingDraft.Revision));
        var extraResult = await Handler(extra).Handle(new SubmitListingDraftCommand(extraDraft.Id, extraDraft.Revision));

        Assert.Same(WriteErrors.PrerequisitesUnverified, missingResult.Error);
        Assert.Same(WriteErrors.PrerequisitesUnverified, extraResult.Error);
        AssertUnchanged(missing, missingDraft);
        AssertUnchanged(extra, extraDraft);
    }

    [Fact]
    public async Task TheInternalVerificationCategoryStillDistinguishesAnUnavailableProviderFromAFailedOne()
    {
        var draft = _world.SeedDraft(evidenceCount: 1);
        var unavailable = new ScriptedEvidenceProvider { OnAssess = _ => EvidenceAssessmentResult.Unavailable() };
        var rejected = new ScriptedEvidenceProvider
        {
            OnAssess = evidence => EvidenceAssessmentResult.Assessed(evidence.Select(reference => WriteHandlerWorld.Assessment(reference, EvidenceScanStatus.Rejected)).ToArray()),
        };

        var first = await SubmissionEvidenceVerifier.VerifyAsync(draft, unavailable, WriteHandlerWorld.Now);
        var second = await SubmissionEvidenceVerifier.VerifyAsync(draft, rejected, WriteHandlerWorld.Now);

        Assert.Equal(EvidenceVerificationFailure.ProviderUnavailable, first.Failure);
        Assert.Equal(EvidenceVerificationFailure.AssessmentInvalidOrNotClean, second.Failure);
        Assert.Same(first.Result.Error, second.Result.Error);
    }

    [Fact]
    public async Task AProviderFailureAndCancellationPropagateAndNothingIsSubmitted()
    {
        using var source = new CancellationTokenSource();
        var draft = _world.SeedDraft(evidenceCount: 1);
        _world.Evidence.ExceptionToThrow = new HttpRequestException("SENTINEL");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision)));
        AssertUnchanged(_world, draft);

        _world.Evidence.ExceptionToThrow = new OperationCanceledException(source.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision), source.Token));
        AssertUnchanged(_world, draft);
    }

    [Fact]
    public async Task TheCallersTokenReachesTheProvider()
    {
        using var source = new CancellationTokenSource();
        var draft = _world.SeedDraft(evidenceCount: 1);

        await Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision), source.Token);

        Assert.Equal(source.Token, _world.Evidence.LastToken);
    }

    [Fact]
    public async Task AStaleRevisionIsRefusedBeforeTheProviderIsAsked()
    {
        var draft = _world.SeedDraft(evidenceCount: 1);

        foreach (var expected in new[] { 0, 2, 99 })
        {
            var result = await Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, expected));

            Assert.Same(WriteErrors.StaleRevision, result.Error);
        }

        Assert.Equal(0, _world.Evidence.AssessCount);
        AssertUnchanged(_world, draft);
    }

    [Fact]
    public async Task ASubmittedDraftCannotBeSubmittedAgainOrChanged()
    {
        var draft = _world.SeedDraft(evidenceCount: 1);
        await Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision));
        var revision = draft.Revision;
        var assessments = draft.SubmittedAssessments;
        var assessedBefore = _world.Evidence.AssessCount;

        var again = await Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, revision));

        Assert.Same(WriteErrors.StaleRevision, again.Error);
        Assert.Equal(revision, draft.Revision);
        Assert.Same(assessments, draft.SubmittedAssessments);
        Assert.Equal(assessedBefore, _world.Evidence.AssessCount);
        Assert.Equal(1, _world.Repository.SaveCount);
    }

    [Fact]
    public async Task AMissingDraftAndSomeoneElsesDraftAreTheSameNotFound()
    {
        var other = new WriteHandlerWorld(acting: Guid.Parse("99999999-9999-9999-9999-999999999999"));
        var othersDraft = other.SeedDraft(evidenceCount: 1);

        var missing = await Handler(other).Handle(new SubmitListingDraftCommand(Guid.NewGuid(), 1));
        var notMine = await Handler(other).Handle(new SubmitListingDraftCommand(othersDraft.Id, 1));

        Assert.Same(ListingSubmissionErrors.NotFound, missing.Error);
        Assert.Same(ListingSubmissionErrors.NotFound, notMine.Error);
        Assert.Equal(ListingSubmissionStatus.Draft, othersDraft.Status);
        Assert.Equal(0, other.Evidence.AssessCount);
    }

    [Fact]
    public async Task AnonymousAndInactiveAccountsAreRefusedBeforeAnythingIsRead()
    {
        var anonymous = new WriteHandlerWorld(anonymous: true);
        var missing = new WriteHandlerWorld(accountStatus: null);
        var pending = new WriteHandlerWorld(accountStatus: UserStatus.Pending);
        var id = Guid.NewGuid();

        var anonymousResult = await Handler(anonymous).Handle(new SubmitListingDraftCommand(id, 1));
        var missingResult = await Handler(missing).Handle(new SubmitListingDraftCommand(id, 1));
        var pendingResult = await Handler(pending).Handle(new SubmitListingDraftCommand(id, 1));

        Assert.Same(ListingSubmissionErrors.Unauthorized, anonymousResult.Error);
        Assert.Same(ListingSubmissionErrors.Unauthorized, missingResult.Error);
        Assert.Same(MeErrors.AccountUnavailable, pendingResult.Error);
        Assert.All(new[] { anonymous, missing, pending }, world => Assert.Empty(world.Repository.Calls));
    }

    [Fact]
    public async Task AGrantThatIsNoLongerInForceStopsTheSubmissionBeforeAnythingIsAssessed()
    {
        foreach (var grant in new AuthorityGrantView?[]
        {
            null,
            WriteHandlerWorld.OwnerGrant() with { State = AuthorityGrantState.Revoked, RevokedAt = WriteHandlerWorld.Now.AddMinutes(-1) },
            WriteHandlerWorld.OwnerGrant() with { ValidUntil = WriteHandlerWorld.Now },
            WriteHandlerWorld.OwnerGrant() with { LegalReviewedByUserId = WriteHandlerWorld.Issuer },
        })
        {
            var world = new WriteHandlerWorld();
            var draft = world.SeedDraft(evidenceCount: 1);
            world.Repository.Grant = grant;

            var result = await Handler(world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision));

            Assert.Same(WriteErrors.NotEligible, result.Error);
            Assert.Equal(0, world.Evidence.AssessCount);
            AssertUnchanged(world, draft);
        }
    }

    [Fact]
    public async Task AGrantForADifferentPartyThanTheDraftsAuthorityIsNotEligible()
    {
        var draft = _world.SeedDraft(evidenceCount: 1);
        _world.Repository.Grant = WriteHandlerWorld.OwnerGrant() with { LegalPartyReferenceId = Guid.NewGuid() };

        var result = await Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision));

        Assert.Same(WriteErrors.NotEligible, result.Error);
        Assert.Equal(0, _world.Evidence.AssessCount);
        AssertUnchanged(_world, draft);
    }

    [Fact]
    public async Task AGrantForAnotherPurposeIsNotEligible()
    {
        var draft = _world.SeedDraft(evidenceCount: 1);
        _world.Repository.Grant = WriteHandlerWorld.OwnerGrant() with { AllowsSale = false, AllowsRent = true };

        var result = await Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision));

        Assert.Same(WriteErrors.NotEligible, result.Error);
        AssertUnchanged(_world, draft);
    }

    [Fact]
    public async Task APropertyThatChangedSinceTheDraftWasWrittenIsRefused()
    {
        var draft = _world.SeedDraft(evidenceCount: 1);
        _world.Repository.Property = WriteHandlerWorld.Property() with { City = "Jeddah" };

        var result = await Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision));

        Assert.Same(WriteErrors.PropertyChanged, result.Error);
        Assert.Equal(0, _world.Evidence.AssessCount);
        AssertUnchanged(_world, draft);
    }

    [Fact]
    public async Task AnArchivedOrReservedPropertyIsToldOnlyToAnEligibleUser()
    {
        var draft = _world.SeedDraft(evidenceCount: 1);
        _world.Repository.Property = WriteHandlerWorld.Property() with { IsActive = false };

        var eligible = await Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision));

        Assert.Same(WriteErrors.PropertyUnavailable, eligible.Error);

        var ineligible = new WriteHandlerWorld();
        var other = ineligible.SeedDraft(evidenceCount: 1);
        ineligible.Repository.Property = WriteHandlerWorld.Property() with { IsActive = false, HasMarketplaceReservation = true };
        ineligible.Repository.Grant = null;

        var refused = await Handler(ineligible).Handle(new SubmitListingDraftCommand(other.Id, other.Revision));

        Assert.Same(WriteErrors.NotEligible, refused.Error);
    }

    [Fact]
    public async Task ALostRaceOnTheRevisionIsStaleAndOtherConflictsPropagate()
    {
        var draft = _world.SeedDraft(evidenceCount: 1);
        _world.Repository.ExceptionOnSave = new ListingWriteConflictException(ListingWriteConflictKind.StaleRevision);

        var result = await Handler(_world).Handle(new SubmitListingDraftCommand(draft.Id, draft.Revision));

        Assert.Same(WriteErrors.StaleRevision, result.Error);
        Assert.Empty(_world.Repository.CommittedAudit);

        var other = new WriteHandlerWorld();
        var otherDraft = other.SeedDraft(evidenceCount: 1);
        other.Repository.ExceptionOnSave = new ListingWriteConflictException(ListingWriteConflictKind.ListingSlot);
        await Assert.ThrowsAsync<ListingWriteConflictException>(
            () => Handler(other).Handle(new SubmitListingDraftCommand(otherDraft.Id, otherDraft.Revision)));

        var broken = new WriteHandlerWorld();
        var brokenDraft = broken.SeedDraft(evidenceCount: 1);
        broken.Repository.ExceptionOnSave = new TimeoutException();
        await Assert.ThrowsAsync<TimeoutException>(
            () => Handler(broken).Handle(new SubmitListingDraftCommand(brokenDraft.Id, brokenDraft.Revision)));
    }

    [Fact]
    public async Task TheCommandCarriesNoVerificationAndThePublicErrorSaysNothingAboutWhyItFailed()
    {
        await Task.CompletedTask;

        Assert.Equal(
            new[] { "SubmissionId", "ExpectedRevision" },
            typeof(SubmitListingDraftCommand).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name!).ToArray());

        foreach (var word in new[] { "evidence", "scan", "clean", "provider", "assessment", "digest", "unavailable" })
        {
            Assert.DoesNotContain(word, WriteErrors.PrerequisitesUnverified.Description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AnInvalidRequestIsRefusedBeforeAnyRead()
    {
        var result = await Handler(_world).Handle(new SubmitListingDraftCommand(Guid.Empty, 1));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Empty(_world.Repository.Calls);
    }

    private static void AssertUnchanged(WriteHandlerWorld world, ListingSubmission draft)
    {
        Assert.Equal(ListingSubmissionStatus.Draft, draft.Status);
        Assert.Equal(1, draft.Revision);
        Assert.Empty(draft.SubmittedAssessments);
        Assert.Null(draft.SubmittedAt);
        Assert.Equal(0, world.Repository.SaveCount);
    }

    private static SubmitListingDraftCommandHandler Handler(WriteHandlerWorld world)
    {
        return new SubmitListingDraftCommandHandler(world.CurrentUser, world.Users, world.Repository, world.Evidence, world.Clock);
    }
}
