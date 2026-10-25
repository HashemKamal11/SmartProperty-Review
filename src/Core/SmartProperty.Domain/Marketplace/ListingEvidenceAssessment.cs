namespace SmartProperty.Domain.Marketplace;

/// <summary>
/// Immutable snapshot of a trusted security-scanning result, not a malware scanner.
/// The Application layer must supply the current authentic assessment; historic Clean results
/// do not establish freshness, legal sufficiency or absence of later adverse findings.
/// </summary>
public sealed class ListingEvidenceAssessment
{
    public ListingEvidenceAssessment(
        Guid assessmentId, ListingEvidenceReference evidence, EvidenceScanStatus status, DateTimeOffset assessedAt)
    {
        if (assessmentId == Guid.Empty)
            throw new ArgumentException("Assessment identity must be nonempty.", nameof(assessmentId));
        ArgumentNullException.ThrowIfNull(evidence);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));

        AssessmentId = assessmentId;
        Evidence = evidence;
        Status = status;
        AssessedAt = assessedAt;
    }

    public Guid AssessmentId { get; }
    public ListingEvidenceReference Evidence { get; }
    public EvidenceScanStatus Status { get; }
    public DateTimeOffset AssessedAt { get; }

    public void EnsureCleanFor(ListingEvidenceReference evidence, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (Evidence.EvidenceId != evidence.EvidenceId || Evidence.VersionId != evidence.VersionId
            || Evidence.PropertyId != evidence.PropertyId || Evidence.Purpose != evidence.Purpose
            || !string.Equals(Evidence.Sha256, evidence.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException("The scan assessment does not match the exact evidence reference.");
        if (Status != EvidenceScanStatus.Clean || AssessedAt > at)
            throw new InvalidOperationException("A matching Clean assessment available at the evaluation time is required.");
    }
}
