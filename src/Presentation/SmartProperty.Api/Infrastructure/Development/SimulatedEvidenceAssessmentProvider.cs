#nullable enable
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Api.Infrastructure.Development;

/// <summary>
/// The Development-only stand-in for evidence verification. It recognises <b>only</b> fixtures registered through the
/// demo fixture endpoint; every other id fails.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolving.</b> An id resolves only if it names a registered fixture that was registered by the acting user for this
/// very property. An unknown id, another user's fixture or another property's fixture is simply not returned, and the
/// caller's own check that every requested id came back then refuses the draft.
/// </para>
/// <para>
/// <b>Assessing.</b> A reference is assessed only if it matches the registered fixture exactly (id, version, property,
/// digest) <b>and</b> the stored file still has that digest. A fixture containing the reject marker is Rejected; a missing
/// or altered file is Failed; anything unknown is left out, so the submission is refused. Nothing is scanned by a real
/// engine: this is a simulation and says so in the log.
/// </para>
/// </remarks>
internal sealed class SimulatedEvidenceAssessmentProvider(
    EvidenceFixtureStore fixtures,
    ILogger<SimulatedEvidenceAssessmentProvider> logger) : IEvidenceAssessmentProvider
{
    public async Task<EvidenceResolution> ResolveAsync(
        Guid actingUserId,
        Guid propertyId,
        ListingPurpose purpose,
        IReadOnlyCollection<Guid> evidenceIds,
        CancellationToken cancellationToken = default)
    {
        var references = new List<ListingEvidenceReference>();

        foreach (var id in evidenceIds.Distinct())
        {
            var fixture = await fixtures.FindAsync(id, cancellationToken);

            if (fixture is null || fixture.RegisteredByUserId != actingUserId || fixture.PropertyId != propertyId)
            {
                logger.LogWarning("SIMULATED evidence: id {EvidenceId} is not a fixture registered by this user for this property.", id);

                continue;
            }

            references.Add(new ListingEvidenceReference(fixture.EvidenceId, fixture.VersionId, fixture.PropertyId, purpose, fixture.Sha256));
        }

        return EvidenceResolution.Resolved(references);
    }

    public async Task<EvidenceAssessmentResult> AssessAsync(
        IReadOnlyCollection<ListingEvidenceReference> evidence,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var assessments = new List<ListingEvidenceAssessment>();

        foreach (var reference in evidence)
        {
            var fixture = await fixtures.FindAsync(reference.EvidenceId, cancellationToken);

            if (fixture is null
                || fixture.VersionId != reference.VersionId
                || fixture.PropertyId != reference.PropertyId
                || !string.Equals(fixture.Sha256, reference.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("SIMULATED evidence: reference {EvidenceId} matches no registered fixture and is not assessed.", reference.EvidenceId);

                continue;
            }

            var (intact, marked) = await fixtures.InspectAsync(fixture, cancellationToken);
            var status = !intact ? EvidenceScanStatus.Failed : marked ? EvidenceScanStatus.Rejected : EvidenceScanStatus.Clean;

            logger.LogWarning("SIMULATED evidence assessment (no real scan): {EvidenceId} is {Status}.", reference.EvidenceId, status);
            assessments.Add(new ListingEvidenceAssessment(Guid.NewGuid(), reference, status, at));
        }

        return EvidenceAssessmentResult.Assessed(assessments);
    }
}
