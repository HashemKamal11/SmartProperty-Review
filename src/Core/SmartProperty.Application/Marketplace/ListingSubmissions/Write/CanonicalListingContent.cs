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
                input.RentPeriod));
        }
        catch (ArgumentException)
        {
            // The Domain refused the content (for instance a rent period that does not fit the purpose). The request
            // validators check the same rules, so this is only a backstop and it names nothing.
            return Result<ListingSubmissionContent>.Failure(
                ListingSubmissionErrors.ValidationFailed("The listing content is invalid."));
        }
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
