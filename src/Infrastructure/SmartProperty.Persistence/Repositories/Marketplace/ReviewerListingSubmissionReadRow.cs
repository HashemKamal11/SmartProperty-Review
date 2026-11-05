namespace SmartProperty.Persistence.Repositories.Marketplace;

// Unmapped SQL projection for the administrative review reads. It holds only values the review read models may show.
// It deliberately has no member for a rejection reason, a reviewer or assessment snapshot, a legal-party reference,
// a user or Workspace identity, or an evidence identifier or digest: those are never selected, so they cannot leak.
internal sealed class ReviewerListingSubmissionReadRow
{
    public Guid SubmissionId { get; set; }
    public Guid ListingId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid? PredecessorSubmissionId { get; set; }
    public string PurposeText { get; set; } = null!;
    public int Version { get; set; }
    public string StatusText { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string Description { get; set; } = null!;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = null!;
    public int? RentPeriod { get; set; }
    public int PropertyType { get; set; }
    public string Country { get; set; } = null!;
    public string Region { get; set; } = null!;
    public string City { get; set; } = null!;
    public string District { get; set; } = null!;
    public int Representation { get; set; }
    public int OwnerKind { get; set; }
    public DateTimeOffset AuthorityValidFrom { get; set; }
    public DateTimeOffset? AuthorityValidUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int EvidenceCount { get; set; }
    public bool AllEvidenceClean { get; set; }
    public bool IsCurrentlyPublished { get; set; }
    public bool? DecisionApproved { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}
