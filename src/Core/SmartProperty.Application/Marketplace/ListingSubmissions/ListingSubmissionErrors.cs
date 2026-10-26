using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.ListingSubmissions;

internal static class ListingSubmissionErrors
{
    /// <summary>
    /// Returned when the request carries no usable authenticated user. Matches the code and message the JWT Bearer
    /// challenge produces, so it looks the same to a client as any other unauthenticated request.
    /// </summary>
    public static readonly Error Unauthorized = new(
        "authentication.unauthorized",
        "Authentication is required.",
        ErrorType.Unauthorized);

    /// <summary>
    /// The one answer for a submission the caller cannot see, whatever the reason: it does not exist, or it belongs
    /// to someone else. The caller cannot tell the cases apart.
    /// </summary>
    public static readonly Error NotFound = new(
        "listing_submissions.not_found",
        "The listing submission was not found.",
        ErrorType.NotFound);

    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
