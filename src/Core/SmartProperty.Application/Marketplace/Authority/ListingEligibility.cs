namespace SmartProperty.Application.Marketplace.Authority;

/// <summary>
/// The result of an eligibility evaluation: eligible with the facts it established, not eligible with an internal
/// reason, or unavailable because the trusted source could not answer.
/// </summary>
/// <remarks>
/// Only <see cref="Eligible"/> carries facts, and it can only be built with facts that say the legal authority is
/// verified. Construction goes through the factories, so there is no way to hold an "eligible" result without them.
/// <see cref="Unavailable"/> is deliberately its own outcome: an outage is not a refusal of the user and must never
/// become an approval. Callers allocate nothing unless the outcome is <see cref="ListingEligibilityOutcome.Eligible"/>.
/// </remarks>
public sealed class ListingEligibility
{
    private ListingEligibility(
        ListingEligibilityOutcome outcome,
        ListingAuthorityFacts? authority,
        SubmissionVerificationFacts? submissionFacts,
        ListingEligibilityDenial? denial)
    {
        Outcome = outcome;
        Authority = authority;
        SubmissionFacts = submissionFacts;
        Denial = denial;
    }

    public ListingEligibilityOutcome Outcome { get; }

    /// <summary>Present only when eligible.</summary>
    public ListingAuthorityFacts? Authority { get; }

    /// <summary>Present only when eligible.</summary>
    public SubmissionVerificationFacts? SubmissionFacts { get; }

    /// <summary>Present only when not eligible. Never shown to the caller.</summary>
    public ListingEligibilityDenial? Denial { get; }

    public bool IsEligible => Outcome == ListingEligibilityOutcome.Eligible;

    public static ListingEligibility Eligible(ListingAuthorityFacts authority, SubmissionVerificationFacts submissionFacts)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(submissionFacts);

        if (!submissionFacts.LegalAuthorityVerified)
        {
            throw new ArgumentException("An eligible result requires verified legal authority.", nameof(submissionFacts));
        }

        return new ListingEligibility(ListingEligibilityOutcome.Eligible, authority, submissionFacts, denial: null);
    }

    public static ListingEligibility NotEligible(ListingEligibilityDenial denial)
    {
        if (!Enum.IsDefined(denial))
        {
            throw new ArgumentOutOfRangeException(nameof(denial));
        }

        return new ListingEligibility(ListingEligibilityOutcome.NotEligible, authority: null, submissionFacts: null, denial);
    }

    public static ListingEligibility Unavailable()
    {
        return new ListingEligibility(ListingEligibilityOutcome.Unavailable, authority: null, submissionFacts: null, denial: null);
    }
}
