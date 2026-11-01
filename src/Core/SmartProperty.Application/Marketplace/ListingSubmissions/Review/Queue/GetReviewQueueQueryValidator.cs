using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Review.Queue;

internal static class GetReviewQueueQueryValidator
{
    public static Error? Validate(GetReviewQueueQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Purpose is { } purpose && !Enum.IsDefined(purpose))
        {
            return ListingSubmissionErrors.ValidationFailed("The listing purpose is not supported.");
        }

        if (query.Status is { } status)
        {
            if (!Enum.IsDefined(status))
            {
                return ListingSubmissionErrors.ValidationFailed("The submission status is not supported.");
            }

            // A draft has not been submitted, so it can never be in a review queue.
            if (status == ListingSubmissionStatus.Draft)
            {
                return ListingSubmissionErrors.ValidationFailed("Draft submissions are not reviewable.");
            }
        }

        return null;
    }
}
