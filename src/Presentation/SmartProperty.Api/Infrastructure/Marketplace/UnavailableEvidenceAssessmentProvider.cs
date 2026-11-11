using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Api.Infrastructure.Marketplace;

/// <summary>
/// The production evidence provider until a real evidence pipeline exists: it can never answer.
/// </summary>
/// <remarks>
/// This is not a stand-in for the pipeline. It invents no reference, never reports a scan result and never reports
/// evidence as Clean, so a listing can be drafted without evidence but cannot gain evidence, be submitted or be
/// published. Callers receive <c>listing_submissions.evidence_verification_unavailable</c>. A fake provider that
/// answers is for tests only and is registered only there.
/// </remarks>
internal sealed class UnavailableEvidenceAssessmentProvider : IEvidenceAssessmentProvider
{
    public Task<EvidenceResolution> ResolveAsync(
        Guid actingUserId,
        Guid propertyId,
        ListingPurpose purpose,
        IReadOnlyCollection<Guid> evidenceIds,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(EvidenceResolution.Unavailable());
    }

    public Task<EvidenceAssessmentResult> AssessAsync(
        IReadOnlyCollection<ListingEvidenceReference> evidence,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(EvidenceAssessmentResult.Unavailable());
    }
}
