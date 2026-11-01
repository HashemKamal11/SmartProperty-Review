using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// Why a draft's evidence could not be verified. Internal, for logging and operational handling only: it is never
/// part of a response, so a client cannot tell these apart.
/// </summary>
/// <remarks>
/// The distinction that matters operationally is between <see cref="ProviderUnavailable"/>, where the trusted source
/// could not answer and the same request may succeed later, and the others, where it answered and the answer was not
/// good enough. Each value is a fixed category: nothing here carries an identifier, a digest or any provider detail.
/// </remarks>
internal enum EvidenceVerificationFailure
{
    /// <summary>The trusted provider could not answer.</summary>
    ProviderUnavailable = 1,

    /// <summary>The draft has no evidence at all.</summary>
    NoEvidence = 2,

    /// <summary>
    /// The assessments are not exactly one per piece of evidence: one is missing, one is extra, or a piece has two.
    /// </summary>
    AssessmentMissing = 3,

    /// <summary>
    /// An assessment exists for each piece but one does not match the exact reference, is not Clean, or is later
    /// than the evaluation time.
    /// </summary>
    AssessmentInvalidOrNotClean = 4
}

/// <summary>
/// The outcome of verifying a draft's evidence: the public result, and the internal reason when it failed.
/// </summary>
/// <remarks>
/// <see cref="Result"/> is what the caller acts on, and every failure in it is the same public error,
/// <see cref="WriteErrors.PrerequisitesUnverified"/>. <see cref="Failure"/> is null exactly when verification
/// succeeded. A handler logs the category and never returns it.
/// </remarks>
internal sealed record EvidenceVerification(
    Result<IReadOnlyList<ListingEvidenceAssessment>> Result,
    EvidenceVerificationFailure? Failure);

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
/// are all the same public <see cref="WriteErrors.PrerequisitesUnverified"/>, and nothing in a response says which.
/// The internal <see cref="EvidenceVerificationFailure"/> does.
/// </para>
/// <para>
/// <b>Exceptions are not evidence failures.</b> A provider that throws is an infrastructure fault, not an answer, and
/// its exception propagates untouched, including cancellation. The only exception caught is the Domain's
/// <see cref="InvalidOperationException"/> from its own assessment check, which here means "this assessment is not
/// acceptable". A caller that logs a propagated provider fault should record only the exception type, never its
/// message or data, since either may carry provider detail.
/// </para>
/// </remarks>
internal static class SubmissionEvidenceVerifier
{
    public static async Task<EvidenceVerification> VerifyAsync(
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
            return Unverified(EvidenceVerificationFailure.NoEvidence);
        }

        var result = await provider.AssessAsync(references.ToArray(), at, cancellationToken);

        if (!result.IsAvailable)
        {
            return Unverified(EvidenceVerificationFailure.ProviderUnavailable);
        }

        var assessments = result.Assessments!;

        if (assessments.Count != references.Count)
        {
            return Unverified(EvidenceVerificationFailure.AssessmentMissing);
        }

        foreach (var reference in references)
        {
            var matching = assessments.Where(assessment => assessment.Evidence.EvidenceId == reference.EvidenceId).ToArray();

            if (matching.Length != 1)
            {
                return Unverified(EvidenceVerificationFailure.AssessmentMissing);
            }

            try
            {
                matching[0].EnsureCleanFor(reference, at);
            }
            catch (InvalidOperationException)
            {
                return Unverified(EvidenceVerificationFailure.AssessmentInvalidOrNotClean);
            }
        }

        return new EvidenceVerification(Result<IReadOnlyList<ListingEvidenceAssessment>>.Success(assessments), Failure: null);
    }

    private static EvidenceVerification Unverified(EvidenceVerificationFailure failure)
    {
        return new EvidenceVerification(
            Result<IReadOnlyList<ListingEvidenceAssessment>>.Failure(WriteErrors.PrerequisitesUnverified),
            failure);
    }
}
