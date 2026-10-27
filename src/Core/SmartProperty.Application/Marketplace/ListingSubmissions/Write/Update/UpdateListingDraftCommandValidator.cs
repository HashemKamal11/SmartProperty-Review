using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Update;

internal static class UpdateListingDraftCommandValidator
{
    public static Error? Validate(UpdateListingDraftCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.SubmissionId == Guid.Empty)
        {
            return ListingSubmissionErrors.ValidationFailed("The submission is required.");
        }

        if (command.ExpectedRevision < 0)
        {
            return ListingSubmissionErrors.ValidationFailed("The expected revision must not be negative.");
        }

        // The draft's purpose is not part of the request, so the rent period is checked against it by the handler.
        return ListingDraftContentValidator.Validate(command.Content, purpose: null);
    }
}
