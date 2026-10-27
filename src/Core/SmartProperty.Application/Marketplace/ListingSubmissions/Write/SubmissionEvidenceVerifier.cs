using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// Establishes, before a submission is attempted, that a draft's evidence is complete and clean, and returns the
/// assessments the Domain's <c>Submit</c> needs.
/// </summary>
/// <remarks>
/// <para>
/// A draft may have no evidence yet, but a submission may not, so zero evidence is refused here explicitly: the
/// Domain's own check compares the number of assessments with the number of references, and zero equals zero.
/// </para>
/// <para>
/// The assessments come from the trusted provider and from nowhere else. Each reference must have exactly one
/// assessment that matches it precisely (evidence, version, property, purpose and digest), is Clean, and is not later
/// than the evaluation time; that is the Domain's own test, applied here so a failure is a typed conflict and not an
/// exception. An unavailable provider, a missing assessment, a duplicate, a mismatch or a verdict that is not Clean
/// are all the same <see cref="WriteErrors.PrerequisitesUnverified"/>, and nothing says which.
/// </para>
/// </remarks>
internal static class SubmissionEvidenceVerifier
{
    public static async Task<Result<IReadOnlyList<ListingEvidenceAssessment>>> VerifyAsync(
        ListingSubmission draft,
        IEvidenceAssessmentProvider provider,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(provider);

        var references = draft.Evidence;

        if (references.Count == 0)
        {
            return Unverified();
        }

        var result = await provider.AssessAsync(references.ToArray(), at, cancellationToken);

        if (!result.IsAvailable)
        {
            return Unverified();
        }

        var assessments = result.Assessments!;

        if (assessments.Count != references.Count)
        {
            return Unverified();
        }

        foreach (var reference in references)
        {
            var matching = assessments.Where(assessment => assessment.Evidence.EvidenceId == reference.EvidenceId).ToArray();

            if (matching.Length != 1)
            {
                return Unverified();
            }

            try
            {
                matching[0].EnsureCleanFor(reference, at);
            }
            catch (InvalidOperationException)
            {
                return Unverified();
            }
        }

        return Result<IReadOnlyList<ListingEvidenceAssessment>>.Success(assessments);
    }

    private static Result<IReadOnlyList<ListingEvidenceAssessment>> Unverified()
    {
        return Result<IReadOnlyList<ListingEvidenceAssessment>>.Failure(WriteErrors.PrerequisitesUnverified);
    }
}
