namespace SmartProperty.Application.Marketplace.ListingSubmissions.Review;

/// <summary>The ways a review detail lookup can end.</summary>
public enum ReviewDetailOutcome
{
    /// <summary>The submission exists and the reviewer is independent of it. A detail is present.</summary>
    Found = 1,

    /// <summary>No such submission.</summary>
    NotFound = 2,

    /// <summary>The reviewer is the submitter, the declared owner, or affiliated with the submitting Workspace.</summary>
    NotIndependent = 3,

    /// <summary>The facts needed to establish independence are missing or unknown. Treated as not independent.</summary>
    IndependenceUnverifiable = 4,
}

/// <summary>
/// The result of asking for one submission for review: either the detail, or the reason it cannot be shown.
/// </summary>
/// <remarks>
/// A detail is present exactly when the outcome is <see cref="ReviewDetailOutcome.Found"/>. The factories are the
/// only way to build one, so no other combination can exist.
/// </remarks>
public sealed class ReviewDetailLookup
{
    private ReviewDetailLookup(ReviewDetailOutcome outcome, ReviewSubmissionDetail? detail)
    {
        Outcome = outcome;
        Detail = detail;
    }

    public ReviewDetailOutcome Outcome { get; }

    public ReviewSubmissionDetail? Detail { get; }

    public static ReviewDetailLookup Found(ReviewSubmissionDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        return new ReviewDetailLookup(ReviewDetailOutcome.Found, detail);
    }

    public static ReviewDetailLookup NotFound()
    {
        return new ReviewDetailLookup(ReviewDetailOutcome.NotFound, null);
    }

    public static ReviewDetailLookup NotIndependent()
    {
        return new ReviewDetailLookup(ReviewDetailOutcome.NotIndependent, null);
    }

    public static ReviewDetailLookup IndependenceUnverifiable()
    {
        return new ReviewDetailLookup(ReviewDetailOutcome.IndependenceUnverifiable, null);
    }
}
