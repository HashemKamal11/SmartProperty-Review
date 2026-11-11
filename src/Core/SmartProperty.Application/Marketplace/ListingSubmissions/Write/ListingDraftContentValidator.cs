using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// Validates draft content against what the Domain already requires, and nothing more.
/// </summary>
/// <remarks>
/// The title, description and currency are required, a rental needs a Daily, Weekly, Monthly or Yearly period, and a sale has none,
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

        var hasOptions = content.PricingOptions is { Count: > 0 };

        if (hasOptions || content.RentalTerms is not null)
        {
            // A rental stated with options: its plain price is derived, so the plain price members must be unused.
            if (purpose == ListingPurpose.Sale)
            {
                return ListingSubmissionErrors.ValidationFailed("A sale must not specify rental pricing options or terms.");
            }

            if (!hasOptions)
            {
                return ListingSubmissionErrors.ValidationFailed("A rental needs at least one pricing option.");
            }

            if (content.RentPeriod is not null)
            {
                return ListingSubmissionErrors.ValidationFailed("A rental with pricing options must not also state a single rent period.");
            }

            if (RentalOptionsValidation(content) is { } optionsError)
            {
                return optionsError;
            }
        }
        else
        {
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

    // The rules the Domain also enforces, stated as friendly messages before anything is built.
    private static Error? RentalOptionsValidation(ListingDraftContentInput content)
    {
        var options = content.PricingOptions!;

        if (options.Count > 4 || options.Any(option => option is null))
        {
            return ListingSubmissionErrors.ValidationFailed("A rental offers at most four pricing options, one per period.");
        }

        if (options.Any(option => !Enum.IsDefined(option.Period)))
        {
            return ListingSubmissionErrors.ValidationFailed("A pricing period must be Daily, Weekly, Monthly or Yearly.");
        }

        if (options.Select(option => option.Period).Distinct().Count() != options.Count)
        {
            return ListingSubmissionErrors.ValidationFailed("A rental offers each pricing period at most once.");
        }

        if (options.Any(option => option.Amount <= 0m))
        {
            return ListingSubmissionErrors.ValidationFailed("Every pricing option needs an amount above zero.");
        }

        if (options.Any(option => option.Currency is null || option.Currency.Length != 3 || !option.Currency.All(c => c is >= 'A' and <= 'Z')))
        {
            return ListingSubmissionErrors.ValidationFailed("Every pricing option needs a currency of exactly three upper-case letters.");
        }

        var terms = content.RentalTerms;

        if (terms is null)
        {
            return ListingSubmissionErrors.ValidationFailed("A rental needs its terms: check-in and check-out times and its available dates.");
        }

        if (terms.MinimumStayDays is < 1 or > RentalTerms.MaxMinimumStayDays)
        {
            return ListingSubmissionErrors.ValidationFailed($"The minimum stay must be between 1 and {RentalTerms.MaxMinimumStayDays} days.");
        }

        var windows = terms.Windows;

        if (windows is null || windows.Count == 0 || windows.Count > RentalTerms.MaxWindows || windows.Any(window => window is null))
        {
            return ListingSubmissionErrors.ValidationFailed($"A rental needs between 1 and {RentalTerms.MaxWindows} windows of available dates.");
        }

        if (windows.Any(window => window.EndDate <= window.StartDate))
        {
            return ListingSubmissionErrors.ValidationFailed("Every window of available dates must end after it starts.");
        }

        var ordered = windows.OrderBy(window => window.StartDate).ToArray();

        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].StartDate < ordered[i - 1].EndDate)
            {
                return ListingSubmissionErrors.ValidationFailed("Windows of available dates must not overlap.");
            }
        }

        return null;
    }
}
