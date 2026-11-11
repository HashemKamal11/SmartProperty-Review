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

    /// <summary>
    /// The reviewer is the submitter, the declared owner, or affiliated with the submitting Workspace. Used by
    /// decisions only; reading a submission for review does not depend on independence.
    /// </summary>
    public static readonly Error NotIndependent = new(
        "listing_submissions.reviewer_not_independent",
        "The reviewer is not independent of this submission.",
        ErrorType.Forbidden);

    /// <summary>
    /// The facts needed to establish the reviewer's independence are missing. Treated as a refusal. Used by
    /// decisions only.
    /// </summary>
    public static readonly Error IndependenceUnverifiable = new(
        "listing_submissions.independence_unverifiable",
        "The reviewer's independence could not be established.",
        ErrorType.Forbidden);

    /// <summary>The submission is not awaiting a decision: it was already decided, withdrawn, superseded, or never submitted.</summary>
    public static readonly Error NotPending = new(
        "listing_submissions.not_pending_review",
        "The submission is not awaiting review.",
        ErrorType.Conflict);

    /// <summary>
    /// The submitter's authority, the property or the submitted content no longer supports publication. The reviewer
    /// is told only that; which condition failed is not disclosed.
    /// </summary>
    public static readonly Error CannotPublish = new(
        "listing_submissions.cannot_publish",
        "The submission can no longer be published.",
        ErrorType.Conflict);

    /// <summary>The property is committed to a sale, so nothing more can be advertised for it until that ends.</summary>
    public static readonly Error PropertyCommitted = new(
        "listing_submissions.property_committed",
        "The property is committed to a sale.",
        ErrorType.Conflict);

    /// <summary>A daily or weekly rental is in force, so the property cannot be advertised for sale until it ends.</summary>
    public static readonly Error ShortStayOccupancy = new(
        "listing_submissions.short_stay_occupancy",
        "A daily or weekly rental is in force, so the property cannot be advertised for sale until it ends.",
        ErrorType.Conflict);
}
