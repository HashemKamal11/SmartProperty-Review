using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Abstractions.Marketplace;

/// <summary>
/// The scan assessments a trusted provider produced for exact evidence references, or the fact that it could not.
/// </summary>
/// <remarks>
/// Assessments are the provider's own findings about the stored bytes. They are never read from a request, and a
/// caller cannot ask for a particular verdict. The Domain accepts an assessment only if it matches the exact
/// reference and is Clean and not later than the evaluation time; an unavailable provider is a separate outcome that
/// must never be treated as Clean.
/// </remarks>
public sealed class EvidenceAssessmentResult
{
    private EvidenceAssessmentResult(IReadOnlyList<ListingEvidenceAssessment>? assessments)
    {
        Assessments = assessments;
    }

    public bool IsAvailable => Assessments is not null;

    /// <summary>The assessments; null exactly when the provider was unavailable.</summary>
    public IReadOnlyList<ListingEvidenceAssessment>? Assessments { get; }

    public static EvidenceAssessmentResult Assessed(IReadOnlyList<ListingEvidenceAssessment> assessments)
    {
        ArgumentNullException.ThrowIfNull(assessments);

        if (assessments.Any(assessment => assessment is null))
        {
            throw new ArgumentException("Assessments must not contain null.", nameof(assessments));
        }

        return new EvidenceAssessmentResult(assessments);
    }

    public static EvidenceAssessmentResult Unavailable()
    {
        return new EvidenceAssessmentResult(assessments: null);
    }
}
