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

    // The trusted evidence source cannot answer, so the request can be neither accepted nor judged. Saying so is not a
    // leak: it describes the platform's state, not the submitter's evidence, and the same request can succeed later.
    public static readonly Error EvidenceVerificationUnavailable = new(
        "listing_submissions.evidence_verification_unavailable",
        "Evidence verification is not available right now, so this listing cannot be saved with evidence, submitted or published. Try again later.",
        ErrorType.ServiceUnavailable);

    // Archived, reserved, or without the location a listing needs. One error for all of them, so it says nothing
    // about which; it is only ever given to a caller who has already been found eligible for the property.
    public static readonly Error PropertyUnavailable = new(
        "listing_submissions.property_unavailable",
        "This property cannot be listed right now.",
        ErrorType.Conflict);

    public static readonly Error PropertyChanged = new(
        "listing_submissions.property_changed",
        "The property changed since this draft was written.",
        ErrorType.Conflict);

    // Only ever given to a caller already found eligible for the property: the rental cannot be dated because the
    // property's country has no recorded time zone. A guess could put every check-in an hour out.
    public static readonly Error ListingNotPublished = new(
        "listing_submissions.listing_not_published",
        "The listing is not published and open.",
        ErrorType.Conflict);

    // Relisting copies the last approved version of a listing that was published and is now closed (withdrawn, or
    // closed by an accepted request). A listing that is open, reserved or was never published has nothing to copy.
    public static readonly Error NotRelistable = new(
        "listing_submissions.not_relistable",
        "Only a published listing that is now closed can be relisted.",
        ErrorType.Conflict);

    // Every availability window of the copied rental has already ended, so the copy would offer no dates at all.
    public static readonly Error RelistDatesPassed = new(
        "listing_submissions.relist_dates_passed",
        "All the rental dates of this listing have passed. Create a new listing with new dates.",
        ErrorType.Conflict);

    public static readonly Error RentalTimeZoneUnavailable = new(
        "listing_submissions.rental_time_zone_unavailable",
        "This property's location has no time zone on record, so a rental cannot be advertised for it yet.",
        ErrorType.Conflict);
}
