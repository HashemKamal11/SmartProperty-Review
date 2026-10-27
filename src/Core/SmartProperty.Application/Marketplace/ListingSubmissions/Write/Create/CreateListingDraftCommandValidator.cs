using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Create;

internal static class CreateListingDraftCommandValidator
{
    public static Error? Validate(CreateListingDraftCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.PropertyId == Guid.Empty)
        {
            return ListingSubmissionErrors.ValidationFailed("The property is required.");
        }

        if (!Enum.IsDefined(command.Purpose))
        {
            return ListingSubmissionErrors.ValidationFailed("The listing purpose is not supported.");
        }

        return ListingDraftContentValidator.Validate(command.Content, command.Purpose);
    }
}
