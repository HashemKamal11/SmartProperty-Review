namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// A versioned private submission snapshot. Application services must supply authenticated actors and
/// current authoritative verification facts, never client assertions. Version uniqueness across concurrent
/// graphs requires later persistence coordination. Database atomicity and authoritative Property locking belong to Application/Persistence.
/// </summary>
public sealed class ListingSubmission
{
    private ListingSubmission() { }

    public ListingSubmission(Guid id, PropertyListing listing, ListingSubmissionContent content,
        ListingLegalAuthority authority, IEnumerable<ListingEvidenceReference> evidence,
        DateTimeOffset createdAt, ListingSubmission? predecessor = null)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(evidence);
        if (id == Guid.Empty) throw new ArgumentException("Submission identity is required.", nameof(id));
        if (content.Purpose != listing.Purpose || authority.Purpose != listing.Purpose || authority.PropertyId != listing.PropertyId)
            throw new ArgumentException("Content and authority must match the listing.");
        if (createdAt < listing.CreatedAt) throw new ArgumentException("Draft cannot precede the listing.", nameof(createdAt));
        if (predecessor is not null)
        {
            if (predecessor.Id == id || predecessor.ListingId != listing.Id || predecessor.PropertyId != listing.PropertyId
                || predecessor.Purpose != listing.Purpose)
                throw new ArgumentException("Invalid predecessor identity.", nameof(predecessor));
            if (predecessor.Status is not (ListingSubmissionStatus.Approved or ListingSubmissionStatus.Rejected or ListingSubmissionStatus.Superseded))
                throw new InvalidOperationException("A successor requires a reviewed predecessor.");
            if (createdAt < predecessor.UpdatedAt) throw new ArgumentException("Draft cannot precede its predecessor.", nameof(createdAt));
        }
        var snapshot = evidence.ToArray();
        if (snapshot.Length == 0 || snapshot.Any(x => x is null))
            throw new ArgumentException("Private evidence is required.", nameof(evidence));
        if (snapshot.Select(x => x.EvidenceId).Distinct().Count() != snapshot.Length)
            throw new ArgumentException("Duplicate evidence identity.", nameof(evidence));
        if (snapshot.Any(x => x.PropertyId != listing.PropertyId || x.Purpose != listing.Purpose))
            throw new ArgumentException("Evidence must cover this property and purpose.", nameof(evidence));

        Version = predecessor is null ? 1 : checked(predecessor.Version + 1);
        Id = id;
        ListingId = listing.Id;
        PropertyId = listing.PropertyId;
        Purpose = listing.Purpose;
        PredecessorId = predecessor?.Id;
        Content = content;
        Authority = authority;
        Evidence = Array.AsReadOnly(snapshot);
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Status = ListingSubmissionStatus.Draft;
    }

    public Guid Id { get; private set; }
    public Guid ListingId { get; private set; }
    public Guid PropertyId { get; private set; }
    public ListingPurpose Purpose { get; private set; }
    public int Version { get; private set; }
    public Guid? PredecessorId { get; private set; }
    public ListingSubmissionContent Content { get; private set; } = null!;
    public ListingLegalAuthority Authority { get; private set; } = null!;
    public IReadOnlyList<ListingEvidenceReference> Evidence { get; private set; } = null!;
    public IReadOnlyList<ListingEvidenceAssessment> SubmittedAssessments { get; private set; } = Array.Empty<ListingEvidenceAssessment>();
    public ListingSubmissionStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }

    public ListingReviewDecision? ReviewDecision { get; private set; }

    /// <summary>
    /// Applies one validated in-memory transition. The caller MUST obtain current trusted facts and load
    /// the current publication under the same Property lock/transaction used by reservation acceptance.
    /// This operation cannot authenticate facts, prove graph completeness or provide database atomicity.
    /// </summary>
    public void ApproveAndPublish(Guid decisionId, PropertyListing listing, ListingSubmission? currentPublication,
        ListingReviewContext reviewer, DateTimeOffset at, bool? propertyAvailable,
        IEnumerable<ListingEvidenceAssessment> assessments, bool? legalAuthorityVerified,
        bool? workspaceOperationAuthorized, bool? actingUserIsDeclaredOwnerVerified = null)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(assessments);
        var snapshot = assessments.ToArray();
        EnsureReview(reviewer, at);
        if (propertyAvailable != true) throw new InvalidOperationException("Current property availability must be verified.");
        if (listing.Id != ListingId || listing.PropertyId != PropertyId || listing.Purpose != Purpose)
            throw new InvalidOperationException("Submission does not belong to this listing.");
        listing.EnsureCanPublishApproved(this, at);
        if (listing.PublishedSubmissionId.HasValue)
        {
            if (currentPublication is null || ReferenceEquals(currentPublication, this)
                || currentPublication.Id != listing.PublishedSubmissionId || currentPublication.ListingId != ListingId
                || currentPublication.PropertyId != PropertyId || currentPublication.Purpose != Purpose
                || currentPublication.Status != ListingSubmissionStatus.Approved
                || currentPublication.ReviewDecision?.Approved != true || Version <= currentPublication.Version
                || at < currentPublication.UpdatedAt)
                throw new InvalidOperationException("The exact current approved version is required.");
        }
        else if (currentPublication is not null)
            throw new InvalidOperationException("Unexpected current publication.");
        Authority.EnsureApplicable(PropertyId, Purpose, Authority.ActingUserId, Authority.WorkspaceId, at,
            legalAuthorityVerified, workspaceOperationAuthorized, actingUserIsDeclaredOwnerVerified);
        if (snapshot.Length != Evidence.Count || snapshot.Any(x => x is null)
            || snapshot.Select(x => x.AssessmentId).Distinct().Count() != snapshot.Length
            || snapshot.Select(x => x.Evidence.EvidenceId).Distinct().Count() != snapshot.Length)
            throw new ArgumentException("Exact current assessments are required.", nameof(assessments));
        foreach (var evidence in Evidence)
        {
            var assessment = snapshot.SingleOrDefault(x => x.Evidence.EvidenceId == evidence.EvidenceId)
                ?? throw new InvalidOperationException("Missing evidence assessment.");
            assessment.EnsureCleanFor(evidence, at);
        }
        var decision = new ListingReviewDecision(decisionId, Id, reviewer, true, null, at, snapshot);
        // No fallible validation remains after this point.
        ReviewDecision = decision;
        Status = ListingSubmissionStatus.Approved;
        UpdatedAt = at;
        if (currentPublication is not null)
        {
            currentPublication.Status = ListingSubmissionStatus.Superseded;
            currentPublication.UpdatedAt = at;
        }
        listing.ApplyApprovedPublication(this, at);
    }

    public void Reject(Guid decisionId, ListingReviewContext reviewer, string reason, DateTimeOffset at)
    {
        EnsureReview(reviewer, at);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var decision = new ListingReviewDecision(decisionId, Id, reviewer, false, reason, at, Array.Empty<ListingEvidenceAssessment>());
        ReviewDecision = decision;
        Status = ListingSubmissionStatus.Rejected;
        UpdatedAt = at;
    }

    private void EnsureReview(ListingReviewContext reviewer, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(reviewer);
        if (Status != ListingSubmissionStatus.PendingReview || ReviewDecision is not null)
            throw new InvalidOperationException("Only an undecided pending submission may be reviewed.");
        if (at < UpdatedAt) throw new ArgumentException("Review cannot precede submission.", nameof(at));
        var owner = Authority.DeclaredOwner;
        if (reviewer.SubmitterUserId != Authority.ActingUserId || reviewer.SubmittingWorkspaceId != Authority.WorkspaceId
            || reviewer.DeclaredOwner.Kind != owner.Kind || reviewer.DeclaredOwner.LegalPartyReferenceId != owner.LegalPartyReferenceId
            || reviewer.DeclaredOwner.LinkedUserId != owner.LinkedUserId)
            throw new InvalidOperationException("Reviewer facts must describe this submission.");
        reviewer.EnsureIndependentReviewer();
    }
    public void Submit(Guid actingUserId, Guid? workspaceId, DateTimeOffset at,
        IEnumerable<ListingEvidenceAssessment> assessments, bool? legalAuthorityVerified,
        bool? workspaceOperationAuthorized, bool? actingUserIsDeclaredOwnerVerified = null)
    {
        ArgumentNullException.ThrowIfNull(assessments);
        var snapshot = assessments.ToArray();
        if (Status != ListingSubmissionStatus.Draft) throw new InvalidOperationException("Only a draft can be submitted.");
        if (at < UpdatedAt) throw new ArgumentException("Submission cannot precede the draft.", nameof(at));
        Authority.EnsureApplicable(PropertyId, Purpose, actingUserId, workspaceId, at,
            legalAuthorityVerified, workspaceOperationAuthorized, actingUserIsDeclaredOwnerVerified);
        if (snapshot.Length != Evidence.Count || snapshot.Any(x => x is null)
            || snapshot.Select(x => x.AssessmentId).Distinct().Count() != snapshot.Length
            || snapshot.Select(x => x.Evidence.EvidenceId).Distinct().Count() != snapshot.Length)
            throw new ArgumentException("One unambiguous assessment per evidence reference is required.", nameof(assessments));
        foreach (var reference in Evidence)
        {
            var assessment = snapshot.SingleOrDefault(x => x.Evidence.EvidenceId == reference.EvidenceId)
                ?? throw new InvalidOperationException("Evidence assessment is missing.");
            assessment.EnsureCleanFor(reference, at);
        }
        var immutableAssessments = Array.AsReadOnly(snapshot);
        SubmittedAssessments = immutableAssessments;
        SubmittedAt = at;
        UpdatedAt = at;
        Status = ListingSubmissionStatus.PendingReview;
    }
}
