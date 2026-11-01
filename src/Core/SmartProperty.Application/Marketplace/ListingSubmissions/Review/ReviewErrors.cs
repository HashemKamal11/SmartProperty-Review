using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Review;

internal static class ReviewErrors
{
    /// <summary>
    /// Returned when the authenticated user is not authorized to read the review queue. Matches the code and
    /// message the API's own authorization failure produces.
    /// </summary>
    public static readonly Error Forbidden = new(
        "authorization.forbidden",
        "You do not have permission to access this resource.",
        ErrorType.Forbidden);

    /// <summary>The reviewer is the submitter, the declared owner, or affiliated with the submitting Workspace.</summary>
    public static readonly Error NotIndependent = new(
        "listing_submissions.reviewer_not_independent",
        "The reviewer is not independent of this submission.",
        ErrorType.Forbidden);

    /// <summary>The facts needed to establish the reviewer's independence are missing. Treated as a refusal.</summary>
    public static readonly Error IndependenceUnverifiable = new(
        "listing_submissions.independence_unverifiable",
        "The reviewer's independence could not be established.",
        ErrorType.Forbidden);
}
