using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// The stable errors of the listing submission write commands.
/// </summary>
/// <remarks>
/// A missing or empty user, a missing submission, and another user's submission use the existing
/// <c>ListingSubmissionErrors.Unauthorized</c> and <c>NotFound</c>. Nothing here names a grant, a condition, a
/// property or a party: the descriptions are generic so a caller cannot map the eligibility model one request at a
/// time. How an unavailable trusted source is reported is decided with the handlers, together with the HTTP mapping.
/// </remarks>
internal static class WriteErrors
{
    public static readonly Error NotEligible = new(
        "listing_submissions.not_eligible",
        "You are not eligible to list this property.",
        ErrorType.Forbidden);

    public static readonly Error OpenRevisionExists = new(
        "listing_submissions.open_revision_exists",
        "An open revision already exists for this listing.",
        ErrorType.Conflict);

    public static readonly Error StaleRevision = new(
        "listing_submissions.stale_revision",
        "The draft was changed by another request.",
        ErrorType.Conflict);

    public static readonly Error PrerequisitesUnverified = new(
        "listing_submissions.prerequisites_unverified",
        "The submission prerequisites could not be verified.",
        ErrorType.Conflict);
}
