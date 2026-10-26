using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.PublicListings;

internal static class PublicListingErrors
{
    /// <summary>
    /// The one answer for a listing that cannot be shown publicly, whatever the reason: it does not exist, is not
    /// published, was withdrawn, is reserved or closed, or has no approved version. The public cannot tell the
    /// cases apart.
    /// </summary>
    public static readonly Error NotFound = new(
        "public_listings.not_found",
        "The listing was not found.",
        ErrorType.NotFound);

    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
