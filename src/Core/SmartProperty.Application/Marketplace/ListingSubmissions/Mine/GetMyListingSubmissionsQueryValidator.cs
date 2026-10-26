using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Mine;

internal static class GetMyListingSubmissionsQueryValidator
{
    public static Error? Validate(GetMyListingSubmissionsQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Purpose is { } purpose && !Enum.IsDefined(purpose))
        {
            return ListingSubmissionErrors.ValidationFailed("The listing purpose is not supported.");
        }

        if (query.Status is { } status && !Enum.IsDefined(status))
        {
            return ListingSubmissionErrors.ValidationFailed("The submission status is not supported.");
        }

        return null;
    }
}
