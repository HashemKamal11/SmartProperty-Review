using SmartProperty.Common.Results;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write.Submit;

internal static class SubmitListingDraftCommandValidator
{
    public static Error? Validate(SubmitListingDraftCommand command)
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

        return null;
    }
}
