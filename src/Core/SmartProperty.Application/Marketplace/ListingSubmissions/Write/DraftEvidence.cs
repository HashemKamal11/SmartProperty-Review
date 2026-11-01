using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// Turns the evidence ids a submitter names into the exact references the trusted provider holds for them.
/// </summary>
/// <remarks>
/// A draft may name no evidence, and then the provider is not asked at all. Otherwise the provider decides what each id
/// means for this user, property and purpose, and the answer is used only if it covers exactly the ids asked for.
/// An id the provider omitted, an extra reference, or one for another property or purpose is the same generic
/// validation failure, so nothing says which ids exist for whom. A provider that cannot answer is the temporary
/// generic <see cref="WriteErrors.PrerequisitesUnverified"/>, and a provider that throws propagates untouched.
/// </remarks>
internal static class DraftEvidence
{
    private static readonly Error NotAvailable =
        ListingSubmissionErrors.ValidationFailed("One or more evidence items are not available.");

    public static async Task<Result<IReadOnlyList<ListingEvidenceReference>>> ResolveAsync(
        IEvidenceAssessmentProvider provider,
        Guid actingUserId,
        Guid propertyId,
        ListingPurpose purpose,
        IReadOnlyList<Guid> evidenceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(evidenceIds);

        if (evidenceIds.Count == 0)
        {
            return Result<IReadOnlyList<ListingEvidenceReference>>.Success([]);
        }

        var resolution = await provider.ResolveAsync(actingUserId, propertyId, purpose, evidenceIds.ToArray(), cancellationToken);

        if (!resolution.IsAvailable)
        {
            return Result<IReadOnlyList<ListingEvidenceReference>>.Failure(WriteErrors.PrerequisitesUnverified);
        }

        var references = resolution.References!;
        var ordered = new List<ListingEvidenceReference>(evidenceIds.Count);

        if (references.Count != evidenceIds.Count)
        {
            return Result<IReadOnlyList<ListingEvidenceReference>>.Failure(NotAvailable);
        }

        foreach (var id in evidenceIds)
        {
            var matches = references.Where(reference => reference.EvidenceId == id).ToArray();

            if (matches.Length != 1 || matches[0].PropertyId != propertyId || matches[0].Purpose != purpose)
            {
                return Result<IReadOnlyList<ListingEvidenceReference>>.Failure(NotAvailable);
            }

            ordered.Add(matches[0]);
        }

        return Result<IReadOnlyList<ListingEvidenceReference>>.Success(ordered);
    }
}
