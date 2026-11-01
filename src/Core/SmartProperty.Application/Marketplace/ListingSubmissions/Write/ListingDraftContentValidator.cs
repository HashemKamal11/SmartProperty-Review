using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// Validates draft content against what the Domain already requires, and nothing more.
/// </summary>
/// <remarks>
/// The title, description and currency are required, a rental needs a Monthly or Yearly period, and a sale has none,
/// as <c>ListingSubmissionContent</c> enforces. Evidence ids must not be empty values or repeated, as the submission
/// enforces. The Domain sets no limit on length, amount or currency, so none is invented here; those are product
/// decisions that belong with the Domain.
/// </remarks>
internal static class ListingDraftContentValidator
{
    /// <param name="content">The content to check.</param>
    /// <param name="purpose">
    /// The listing purpose when it is known (a new draft), so the rent period rule can be checked in full; null when
    /// the draft already exists and its purpose is not part of the request, in which case only the shape of a given
    /// rent period is checked and the handler checks it against the draft.
    /// </param>
    public static Error? Validate(ListingDraftContentInput? content, ListingPurpose? purpose)
    {
        if (content is null)
        {
            return ListingSubmissionErrors.ValidationFailed("The listing content is required.");
        }

        if (string.IsNullOrWhiteSpace(content.Title))
        {
            return ListingSubmissionErrors.ValidationFailed("The title is required.");
        }

        if (string.IsNullOrWhiteSpace(content.Description))
        {
            return ListingSubmissionErrors.ValidationFailed("The description is required.");
        }

        if (string.IsNullOrWhiteSpace(content.Currency))
        {
            return ListingSubmissionErrors.ValidationFailed("The currency is required.");
        }

        if (content.RentPeriod is { } rentPeriod && !Enum.IsDefined(rentPeriod))
        {
            return ListingSubmissionErrors.ValidationFailed("The rent period is not supported.");
        }

        if (purpose == ListingPurpose.Rent && content.RentPeriod is null)
        {
            return ListingSubmissionErrors.ValidationFailed("A rental requires a rent period.");
        }

        if (purpose == ListingPurpose.Sale && content.RentPeriod is not null)
        {
            return ListingSubmissionErrors.ValidationFailed("A sale must not specify a rent period.");
        }

        if (content.EvidenceIds is null)
        {
            return ListingSubmissionErrors.ValidationFailed("The evidence list is required.");
        }

        if (content.EvidenceIds.Any(id => id == Guid.Empty))
        {
            return ListingSubmissionErrors.ValidationFailed("Evidence identifiers must not be empty.");
        }

        if (content.EvidenceIds.Distinct().Count() != content.EvidenceIds.Count)
        {
            return ListingSubmissionErrors.ValidationFailed("Evidence identifiers must not be repeated.");
        }

        return null;
    }
}
