namespace SmartProperty.Domain.Marketplace;

/// <summary>Immutable review history. Supersession never changes the original decision.</summary>
public sealed class ListingReviewDecision
{
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
    public Guid Id { get; }
    public Guid SubmissionId { get; }
    public ListingReviewContext Reviewer { get; }
    public bool Approved { get; }
    public string? Reason { get; }
    public DateTimeOffset DecidedAt { get; }
    public IReadOnlyList<ListingEvidenceAssessment> Assessments { get; }
}
