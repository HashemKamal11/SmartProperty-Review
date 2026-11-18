using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Marketplace;

namespace SmartProperty.Application.Marketplace.ListingSubmissions.Write;

/// <summary>
/// Builds draft content with the location and property type taken from the registry, and tells whether stored content
/// still matches the registry.
/// </summary>
/// <remarks>
/// A submitter claims a title, a description and a price. Where the property is and what kind it is come from the
/// canonical property, never from the request, so the content a reviewer sees cannot contradict the registry. The
/// registry allows a missing region, city or district while a listing requires all of them, so a property without
/// them cannot become a listing: it is refused with <see cref="WriteErrors.PropertyUnavailable"/> rather than given an
/// invented value.
/// </remarks>
internal static class CanonicalListingContent
{
    public static Result<ListingSubmissionContent> Build(
        LockedPropertyView property,
        ListingPurpose purpose,
        ListingDraftContentInput input)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(input);

        if (!HasListingLocation(property))
        {
            return Result<ListingSubmissionContent>.Failure(WriteErrors.PropertyUnavailable);
        }

        try
        {
            if (input.PricingOptions is { Count: > 0 })
            {
                return BuildRental(property, input);
            }

            return Result<ListingSubmissionContent>.Success(new ListingSubmissionContent(
                property.CountryCode,
                property.Region!,
                property.City!,
                property.District!,
                input.Title,
                input.Description,
                property.PropertyType,
                purpose,
                input.Amount,
                input.Currency,
                input.RentPeriod,
                negotiable: input.Negotiable,
                availableFrom: input.AvailableFrom));
        }
        catch (ArgumentException)
        {
            // The Domain refused the content (for instance a rent period that does not fit the purpose). The request
            // validators check the same rules, so this is only a backstop and it names nothing.
            return Result<ListingSubmissionContent>.Failure(
                ListingSubmissionErrors.ValidationFailed("The listing content is invalid."));
        }
    }

    // A rental with pricing options is dated in the property's own time zone, which comes from its country and never
    // from the request. Without one the rental is refused rather than dated by a guess.
    private static Result<ListingSubmissionContent> BuildRental(LockedPropertyView property, ListingDraftContentInput input)
    {
        if (string.IsNullOrWhiteSpace(property.TimeZoneId))
        {
            return Result<ListingSubmissionContent>.Failure(WriteErrors.RentalTimeZoneUnavailable);
        }

        var terms = input.RentalTerms!;
        var rentalTerms = new RentalTerms(
            property.TimeZoneId,
            terms.CheckInTime,
            terms.CheckOutTime,
            terms.Windows.Select(window => new RentalWindow(window.StartDate, window.EndDate)),
            terms.MinimumStayDays);

        return Result<ListingSubmissionContent>.Success(ListingSubmissionContent.ForRent(
            property.CountryCode,
            property.Region!,
            property.City!,
            property.District!,
            input.Title,
            input.Description,
            property.PropertyType,
            input.PricingOptions!.Select(option => new RentPricingOption(option.Period, option.Amount, option.Currency, option.Deposit)),
            rentalTerms));
    }

    /// <summary>
    /// True only when the stored location and property type are exactly what the registry says now.
    /// </summary>
    public static bool Matches(ListingSubmissionContent stored, LockedPropertyView property)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(property);

        return HasListingLocation(property)
            && string.Equals(stored.Country, property.CountryCode, StringComparison.Ordinal)
            && string.Equals(stored.Region, property.Region, StringComparison.Ordinal)
            && string.Equals(stored.City, property.City, StringComparison.Ordinal)
            && string.Equals(stored.District, property.District, StringComparison.Ordinal)
            && stored.PropertyType == property.PropertyType;
    }

    private static bool HasListingLocation(LockedPropertyView property)
    {
        return !string.IsNullOrWhiteSpace(property.CountryCode)
            && !string.IsNullOrWhiteSpace(property.Region)
            && !string.IsNullOrWhiteSpace(property.City)
            && !string.IsNullOrWhiteSpace(property.District);
    }
}
