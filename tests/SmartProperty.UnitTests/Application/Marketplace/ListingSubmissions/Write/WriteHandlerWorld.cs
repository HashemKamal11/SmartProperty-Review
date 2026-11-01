using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Marketplace.Authority;
using SmartProperty.Application.Marketplace.ListingSubmissions.Write;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Marketplace;
using SmartProperty.Domain.PropertyRegistry;
using SmartProperty.UnitTests.Domain.Marketplace;
using SmartProperty.UnitTests.TestDoubles;

namespace SmartProperty.UnitTests.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// One consistent set of stand-ins for the listing write handlers: an Active user, an eligible grant, an active
/// property, an in-memory write repository with a real "tracked, then committed" life cycle, a scripted evidence
/// provider and a recording audit recorder.
/// </summary>
internal sealed class WriteHandlerWorld
{
    public static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid PropertyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Issuer = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid LegalReviewer = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    public const string Digest = "A3F1C9D2B4E5F60718293A4B5C6D7E8F9A0B1C2D3E4F5061728394A5B6C7D8E9";

    public WriteHandlerWorld(Guid? acting = null, UserStatus? accountStatus = UserStatus.Active, bool anonymous = false)
    {
        var actingUser = acting ?? UserId;
        CurrentUser = new FakeCurrentUser(anonymous ? null : actingUser);
        Clock = new FakeDateTimeProvider(Now);
        Repository = new InMemoryWriteRepository
        {
            Property = Property(),
            Grant = OwnerGrant(),
        };
        Audit = new RecordingAuditRecorder(Repository);
        Evidence = new ScriptedEvidenceProvider();

        if (accountStatus is { } status)
        {
            var user = new User(actingUser, $"{actingUser}@example.test", "Some", "One", Now.AddYears(-1));

            switch (status)
            {
                case UserStatus.Active:
                    user.Activate(Now.AddYears(-1));
                    break;
                case UserStatus.Suspended:
                    user.Suspend(Now.AddYears(-1));
                    break;
                case UserStatus.Deactivated:
                    user.Deactivate(Now.AddYears(-1));
                    break;
                case UserStatus.Pending:
                default:
                    break;
            }

            Users.Seed(user);
        }
    }

    public FakeCurrentUser CurrentUser { get; }

    public FakeUserRepository Users { get; } = new();

    public FakeDateTimeProvider Clock { get; }

    public InMemoryWriteRepository Repository { get; }

    public RecordingAuditRecorder Audit { get; }

    public ScriptedEvidenceProvider Evidence { get; }

    public static LockedPropertyView Property()
    {
        return new LockedPropertyView(PropertyId, true, false, PropertyType.Unit, "SA", "Riyadh Province", "Riyadh", "Al Olaya");
    }

    public static AuthorityGrantView OwnerGrant()
    {
        return new AuthorityGrantView(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            7,
            UserId,
            PropertyId,
            AllowsSale: true,
            AllowsRent: true,
            ListingOwnerKind.Individual,
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            LinkedUserId: UserId,
            ListingRepresentationKind.Owner,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 9, 1, 0, 0, 0, TimeSpan.Zero),
            AuthorityGrantState.Active,
            RevokedAt: null,
            Issuer,
            new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero),
            IssuerWasAuthorized: true,
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            LegalReviewer,
            new DateTimeOffset(2026, 8, 15, 9, 0, 0, TimeSpan.Zero));
    }

    public static ListingDraftContentInput Input(
        IReadOnlyList<Guid>? evidenceIds = null,
        string title = "Two bedroom apartment",
        RentPeriod? rentPeriod = null)
    {
        return new ListingDraftContentInput(title, "Close to the centre.", 1250000m, "SAR", rentPeriod, evidenceIds ?? []);
    }

    public static ListingEvidenceReference Reference(
        Guid? evidenceId = null,
        ListingPurpose purpose = ListingPurpose.Sale,
        Guid? propertyId = null)
    {
        return new ListingEvidenceReference(evidenceId ?? Guid.NewGuid(), Guid.NewGuid(), propertyId ?? PropertyId, purpose, Digest);
    }

    public static ListingEvidenceAssessment Assessment(
        ListingEvidenceReference reference,
        EvidenceScanStatus status = EvidenceScanStatus.Clean)
    {
        return new ListingEvidenceAssessment(Guid.NewGuid(), reference, status, Now.AddMinutes(-5));
    }

    /// <summary>A draft by the user, in a listing of its own, with the authority the current grant gives.</summary>
    public ListingSubmission SeedDraft(
        int evidenceCount = 0,
        ListingPurpose purpose = ListingPurpose.Sale,
        bool existingListing = false)
    {
        var listing = Repository.Listing ?? new PropertyListing(Guid.NewGuid(), PropertyId, purpose, Now.AddDays(-5));

        if (!existingListing || Repository.Listing is null)
        {
            Repository.SeedListing(listing);
        }

        return SeedDraftIn(listing, evidenceCount, predecessor: null);
    }

    public ListingSubmission SeedDraftIn(PropertyListing listing, int evidenceCount, ListingSubmission? predecessor)
    {
        var grant = Repository.Grant ?? OwnerGrant();
        var facts = GrantEligibilityEvaluator.Evaluate(grant, UserId, PropertyId, listing.Purpose, Now).Authority!;
        var content = CanonicalListingContent.Build(
            Repository.Property ?? Property(),
            listing.Purpose,
            Input(rentPeriod: listing.Purpose == ListingPurpose.Rent ? RentPeriod.Monthly : null)).Value;
        var evidence = Enumerable.Range(0, evidenceCount).Select(_ => Reference(purpose: listing.Purpose)).ToArray();
        var draft = new ListingSubmission(
            Guid.NewGuid(), listing, content, DraftAuthority.FromFacts(facts), evidence, Now.AddDays(-1), predecessor);

        Repository.SeedSubmission(draft);

        return draft;
    }

    /// <summary>An approved, published version of a listing for the property, as a reviewer would have left it.</summary>
    public (PropertyListing Listing, ListingSubmission Published) SeedPublished(ListingPurpose purpose = ListingPurpose.Sale)
    {
        var listing = MarketplaceFixtures.CreateListing(purpose, PropertyId, publish: false);
        var published = MarketplaceFixtures.Approve(listing, MarketplaceFixtures.PublishedAt);

        Repository.SeedListing(listing);
        Repository.SeedSubmission(published);

        return (listing, published);
    }
}

/// <summary>
/// An in-memory write repository that behaves like the contract: changes are tracked and only
/// <see cref="SaveChangesAsync"/> commits them, together with any audit event recorded since, or none of them.
/// </summary>
internal sealed class InMemoryWriteRepository : IListingSubmissionWriteRepository
{
    private readonly List<ListingSubmission> _submissions = [];
    private readonly List<ListingSubmission> _pendingSubmissions = [];
    private readonly List<ListingSubmission> _pendingRemovedDrafts = [];
    private readonly List<PropertyListing> _pendingListings = [];
    private readonly List<PropertyListing> _pendingRemovedListings = [];

    public List<string> Calls { get; } = [];

    public LockedPropertyView? Property { get; set; }

    public AuthorityGrantView? Grant { get; set; }

    public PropertyListing? Listing { get; private set; }

    public IReadOnlyList<ListingSubmission> Submissions => _submissions;

    public List<ListingDraftDiscardedAuditEvent> PendingAudit { get; } = [];

    public List<ListingDraftDiscardedAuditEvent> CommittedAudit { get; } = [];

    public int SaveCount { get; private set; }

    public Exception? ExceptionOnSave { get; set; }

    public Exception? ExceptionOnRead { get; set; }

    public bool? CanRemoveListingOverride { get; set; }

    public Guid? LastGrantee { get; private set; }

    public CancellationToken LastToken { get; private set; }

    public int PendingChangeCount =>
        _pendingSubmissions.Count + _pendingRemovedDrafts.Count + _pendingListings.Count + _pendingRemovedListings.Count + PendingAudit.Count;

    public void SeedListing(PropertyListing listing)
    {
        Listing = listing;
    }

    public void SeedSubmission(ListingSubmission submission)
    {
        _submissions.Add(submission);
    }

    public Task<LockedPropertyView?> LockPropertyAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        Calls.Add("LockProperty");
        LastToken = cancellationToken;
        ThrowIfScripted();

        return Task.FromResult(Property);
    }

    public Task<AuthorityGrantView?> GetGrantForSharedLockAsync(
        Guid granteeUserId,
        Guid propertyId,
        ListingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("GetGrant");
        LastGrantee = granteeUserId;

        return Task.FromResult(Grant);
    }

    public Task<PropertyListing?> GetListingAsync(Guid propertyId, ListingPurpose purpose, CancellationToken cancellationToken = default)
    {
        Calls.Add("GetListing");
        var listing = Listing is not null && Listing.PropertyId == propertyId && Listing.Purpose == purpose ? Listing : null;

        return Task.FromResult(listing);
    }

    public Task AddListingAsync(PropertyListing listing, CancellationToken cancellationToken = default)
    {
        Calls.Add("AddListing");
        _pendingListings.Add(listing);

        return Task.CompletedTask;
    }

    public Task<ListingSubmission?> GetLatestSubmissionAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        Calls.Add("GetLatest");
        var latest = _submissions.Where(submission => submission.ListingId == listingId).OrderByDescending(submission => submission.Version).FirstOrDefault();

        return Task.FromResult(latest);
    }

    public Task<ListingSubmission?> GetOwnSubmissionAsync(Guid submissionId, Guid actingUserId, CancellationToken cancellationToken = default)
    {
        Calls.Add("GetOwn");
        var own = _submissions.FirstOrDefault(submission => submission.Id == submissionId && submission.Authority.ActingUserId == actingUserId);

        return Task.FromResult(own);
    }

    public Task AddSubmissionAsync(ListingSubmission submission, CancellationToken cancellationToken = default)
    {
        Calls.Add("AddSubmission");
        _pendingSubmissions.Add(submission);

        return Task.CompletedTask;
    }

    public Task RemoveDraftAsync(ListingSubmission draft, CancellationToken cancellationToken = default)
    {
        Calls.Add("RemoveDraft");
        _pendingRemovedDrafts.Add(draft);

        return Task.CompletedTask;
    }

    public Task<bool> CanRemoveListingAfterDraftAsync(Guid listingId, Guid draftSubmissionId, CancellationToken cancellationToken = default)
    {
        Calls.Add("CanRemoveListing");

        if (CanRemoveListingOverride is { } forced)
        {
            return Task.FromResult(forced);
        }

        var others = _submissions.Any(submission => submission.ListingId == listingId && submission.Id != draftSubmissionId);
        var untouched = Listing is { PublicationStatus: ListingPublicationStatus.Unpublished, AvailabilityStatus: ListingAvailabilityStatus.Open, ClosedByReservation: false };

        return Task.FromResult(!others && untouched);
    }

    public Task RemoveListingAsync(PropertyListing listing, CancellationToken cancellationToken = default)
    {
        Calls.Add("RemoveListing");
        _pendingRemovedListings.Add(listing);

        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("Save");
        SaveCount++;
        LastToken = cancellationToken;

        if (ExceptionOnSave is not null)
        {
            // A failed commit applies none of it, audit included.
            ClearPending();

            throw ExceptionOnSave;
        }

        _submissions.AddRange(_pendingSubmissions);
        _submissions.RemoveAll(submission => _pendingRemovedDrafts.Contains(submission));

        if (_pendingListings.Count > 0)
        {
            Listing = _pendingListings[^1];
        }

        if (_pendingRemovedListings.Count > 0)
        {
            Listing = null;
        }

        CommittedAudit.AddRange(PendingAudit);
        ClearPending();

        return Task.CompletedTask;
    }

    private void ClearPending()
    {
        _pendingSubmissions.Clear();
        _pendingRemovedDrafts.Clear();
        _pendingListings.Clear();
        _pendingRemovedListings.Clear();
        PendingAudit.Clear();
    }

    private void ThrowIfScripted()
    {
        if (ExceptionOnRead is not null)
        {
            throw ExceptionOnRead;
        }
    }
}

/// <summary>Adds audit events to the repository's pending changes, never committing them on its own.</summary>
internal sealed class RecordingAuditRecorder(InMemoryWriteRepository repository) : IListingSubmissionAuditRecorder
{
    public Exception? ExceptionToThrow { get; set; }

    public int CallCount { get; private set; }

    public Task RecordAsync(ListingDraftDiscardedAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        CallCount++;
        repository.Calls.Add("Audit");

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        repository.PendingAudit.Add(auditEvent);

        return Task.CompletedTask;
    }
}

/// <summary>An evidence provider that answers exactly as the test scripts it and records what it was asked.</summary>
internal sealed class ScriptedEvidenceProvider : IEvidenceAssessmentProvider
{
    public Func<IReadOnlyCollection<Guid>, EvidenceResolution>? OnResolve { get; set; }

    public Func<IReadOnlyCollection<ListingEvidenceReference>, EvidenceAssessmentResult>? OnAssess { get; set; }

    public Exception? ExceptionToThrow { get; set; }

    public int ResolveCount { get; private set; }

    public int AssessCount { get; private set; }

    public Guid? ResolvedFor { get; private set; }

    public IReadOnlyCollection<Guid>? ResolvedIds { get; private set; }

    public CancellationToken LastToken { get; private set; }

    public Task<EvidenceResolution> ResolveAsync(
        Guid actingUserId,
        Guid propertyId,
        ListingPurpose purpose,
        IReadOnlyCollection<Guid> evidenceIds,
        CancellationToken cancellationToken = default)
    {
        ResolveCount++;
        ResolvedFor = actingUserId;
        ResolvedIds = evidenceIds;
        LastToken = cancellationToken;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        var resolution = OnResolve is null
            ? EvidenceResolution.Resolved(evidenceIds.Select(id => WriteHandlerWorld.Reference(id, purpose, propertyId)).ToArray())
            : OnResolve(evidenceIds);

        return Task.FromResult(resolution);
    }

    public Task<EvidenceAssessmentResult> AssessAsync(
        IReadOnlyCollection<ListingEvidenceReference> evidence,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        AssessCount++;
        LastToken = cancellationToken;

        if (ExceptionToThrow is not null)
        {
            throw ExceptionToThrow;
        }

        var result = OnAssess is null
            ? EvidenceAssessmentResult.Assessed(evidence.Select(reference => WriteHandlerWorld.Assessment(reference)).ToArray())
            : OnAssess(evidence);

        return Task.FromResult(result);
    }
}
