using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Abstractions.Marketplace;

/// <summary>
/// The trusted source of private evidence references and their scan assessments.
/// </summary>
/// <remarks>
/// <para>
/// Implemented by the evidence pipeline, which does not exist yet. There is no implementation and no default in this
/// assembly, and none may be registered that invents references, reports a scan as Clean, or answers "available"
/// when it cannot reach its store. Without a real provider, evidence cannot be attached and a submission cannot
/// succeed, which is the intended fail-closed state.
/// </para>
/// <para>
/// A request carries only evidence ids. The provider decides what those ids mean for this user, property and
/// purpose, and returns exact versions and digests. It is never asked to confirm a verdict the caller supplies.
/// </para>
/// </remarks>
public interface IEvidenceAssessmentProvider
{
    /// <summary>
    /// Resolves evidence ids the acting user holds for this property and purpose to exact references. Ids it does not
    /// recognize or the user does not hold are omitted.
    /// </summary>
    Task<EvidenceResolution> ResolveAsync(
        Guid actingUserId,
        Guid propertyId,
        ListingPurpose purpose,
        IReadOnlyCollection<Guid> evidenceIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces the scan assessment of each exact reference as of the given time.
    /// </summary>
    Task<EvidenceAssessmentResult> AssessAsync(
        IReadOnlyCollection<ListingEvidenceReference> evidence,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);
}
