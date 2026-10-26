namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// A versioned private submission snapshot. Application services must supply authenticated actors and
/// current authoritative verification facts, never client assertions. Version uniqueness across concurrent
/// graphs requires later persistence coordination. This type does not approve or publish listings.
/// </summary>
public sealed class ListingSubmission
{
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

    public Guid Id { get; }
    public Guid ListingId { get; }
    public Guid PropertyId { get; }
    public ListingPurpose Purpose { get; }
    public int Version { get; }
    public Guid? PredecessorId { get; }
    public ListingSubmissionContent Content { get; }
    public ListingLegalAuthority Authority { get; }
    public IReadOnlyList<ListingEvidenceReference> Evidence { get; }
    public IReadOnlyList<ListingEvidenceAssessment> SubmittedAssessments { get; private set; } = Array.Empty<ListingEvidenceAssessment>();
    public ListingSubmissionStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }

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
