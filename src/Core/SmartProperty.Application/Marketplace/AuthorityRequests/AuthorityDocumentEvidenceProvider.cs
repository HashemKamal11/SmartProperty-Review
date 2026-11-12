using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.AuthorityRequests;

/// <summary>
/// The production evidence source: the ownership documents the requester uploaded to an authority request, each one an
/// exact stored version with its SHA-256 and the verdict of the malware scanner.
/// </summary>
/// <remarks>
/// <para>
/// <b>What an evidence id is.</b> A document id from an authority request (<c>documentId</c> in the documents list). An
/// id resolves only to the version in force of a document on a request by the acting user, for this property, that asks
/// for this purpose and has passed legal review (PendingIssuance) or been approved. Anything else is simply omitted,
/// so the caller sees fewer references than ids and fails closed.
/// </para>
/// <para>
/// <b>Assessments.</b> Each exact reference (document, version, property, purpose, digest) is assessed from the stored
/// scan verdict of that version: Clean only when the scanner said so and the version is still in force; a removed or
/// superseded version is Unknown; PendingScan, Rejected and ScanFailed map to Pending, Rejected and Failed. Nothing is
/// ever assumed clean, and nothing is read from a request.
/// </para>
/// </remarks>
public sealed class AuthorityDocumentEvidenceProvider(IAuthorityRequestRepository repository) : IEvidenceAssessmentProvider
{
    public async Task<EvidenceResolution> ResolveAsync(
        Guid actingUserId,
        Guid propertyId,
        ListingPurpose purpose,
        IReadOnlyCollection<Guid> evidenceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidenceIds);

        if (evidenceIds.Count == 0)
        {
            return EvidenceResolution.Resolved([]);
        }

        var rows = await repository.GetCurrentDocumentsAsync(actingUserId, propertyId, evidenceIds, cancellationToken);
        var references = rows
            .Where(row => row.RequestStatus is AuthorityRequestStatus.PendingIssuance or AuthorityRequestStatus.Approved
                && (purpose == ListingPurpose.Sale ? row.RequestsSale : row.RequestsRent))
            .Select(row => new ListingEvidenceReference(row.Document.DocumentId, row.Document.Id, propertyId, purpose, row.Document.Sha256))
            .ToArray();

        return EvidenceResolution.Resolved(references);
    }

    public async Task<EvidenceAssessmentResult> AssessAsync(
        IReadOnlyCollection<ListingEvidenceReference> evidence,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var rows = (await repository.GetDocumentVersionsAsync(evidence.Select(reference => reference.VersionId).ToArray(), cancellationToken))
            .ToDictionary(row => row.Document.Id);
        var assessments = new List<ListingEvidenceAssessment>(evidence.Count);

        foreach (var reference in evidence)
        {
            if (!rows.TryGetValue(reference.VersionId, out var row)
                || row.Document.DocumentId != reference.EvidenceId
                || row.PropertyId != reference.PropertyId
                || !string.Equals(row.Document.Sha256, reference.Sha256, StringComparison.Ordinal))
            {
                // Not the stored version it claims to be: no assessment, so the caller fails closed.
                continue;
            }

            var document = row.Document;
            var status = !document.IsCurrent
                ? EvidenceScanStatus.Unknown
                : document.ScanStatus switch
                {
                    DocumentScanStatus.Clean => EvidenceScanStatus.Clean,
                    DocumentScanStatus.PendingScan => EvidenceScanStatus.Pending,
                    DocumentScanStatus.Rejected => EvidenceScanStatus.Rejected,
                    _ => EvidenceScanStatus.Failed
                };
            var assessedAt = document.ScannedAt is { } scanned && scanned <= at ? scanned : at;

            assessments.Add(new ListingEvidenceAssessment(DeterministicId(document.Id, document.RowVersion), reference, status, assessedAt));
        }

        return EvidenceAssessmentResult.Assessed(assessments);
    }

    // The same stored verdict always yields the same assessment id, so the history records which verdict was used.
    private static Guid DeterministicId(Guid versionId, int rowVersion)
    {
        var bytes = versionId.ToByteArray();
        var version = BitConverter.GetBytes(rowVersion);

        for (var i = 0; i < version.Length; i++)
        {
            bytes[i] ^= version[i];
        }

        bytes[15] ^= 0x5A;

        return new Guid(bytes);
    }
}
