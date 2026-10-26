namespace SmartProperty.Domain.Marketplace;

/// <summary>Immutable review history. Supersession never changes the original decision.</summary>
public sealed class ListingReviewDecision
{
    private ListingReviewDecision() { }

    internal ListingReviewDecision(Guid id, Guid submissionId, ListingReviewContext reviewer,
        bool approved, string? reason, DateTimeOffset at, IReadOnlyList<ListingEvidenceAssessment> assessments)
    {
        if (id == Guid.Empty) throw new ArgumentException("Decision identity is required.", nameof(id));
        Id = id;
        SubmissionId = submissionId;
        Reviewer = reviewer;
        Approved = approved;
        Reason = reason;
        DecidedAt = at;
        Assessments = Array.AsReadOnly(assessments.ToArray());
    }
    public Guid Id { get; private set; }
    public Guid SubmissionId { get; private set; }
    public ListingReviewContext Reviewer { get; private set; } = null!;
    public bool Approved { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset DecidedAt { get; private set; }
    public IReadOnlyList<ListingEvidenceAssessment> Assessments { get; private set; } = null!;
}
