using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Update;

/// <summary>
/// The same rules and messages as creation, so a value accepted by one is accepted by the other.
/// </summary>
internal static class UpdatePropertyCommandValidator
{
    public static Error? Validate(
        UpdatePropertyCommand command,
        out PropertyType propertyType,
        out FinishingStatus? finishingStatus,
        out PropertyListingInfo listing)
    {
        finishingStatus = null;
        listing = PropertyListingInfo.Empty;

        if (!PropertyCommandSupport.TryParseName(command.Type, out propertyType))
        {
            return UpdatePropertyErrors.ValidationFailed(
                $"Type must be one of: {PropertyCommandSupport.NamesOf<PropertyType>()}.");
        }

        if (string.IsNullOrWhiteSpace(command.CountryCode))
        {
            return UpdatePropertyErrors.ValidationFailed("Country code is required.");
        }

        var countryCode = command.CountryCode.Trim();
        if (countryCode.Length != 2
            || !char.IsAsciiLetter(countryCode[0])
            || !char.IsAsciiLetter(countryCode[1]))
        {
            return UpdatePropertyErrors.ValidationFailed("Country code must contain exactly two ASCII letters.");
        }

        if (command.Latitude is < -90m or > 90m)
        {
            return UpdatePropertyErrors.ValidationFailed("Latitude must be between -90 and 90 degrees.");
        }

        if (command.Longitude is < -180m or > 180m)
        {
            return UpdatePropertyErrors.ValidationFailed("Longitude must be between -180 and 180 degrees.");
        }

        if (PropertyCommandSupport.ValidateSpecifications(
                command.Bedrooms,
                command.Bathrooms,
                command.BuiltDate,
                command.FinishingStatus,
                out finishingStatus) is { } specificationError)
        {
            return UpdatePropertyErrors.ValidationFailed(specificationError);
        }

        if (PropertyCommandSupport.ValidateListing(
                command.TransactionType,
                command.Price,
                command.AreaSqm,
                command.Floor,
                command.Furnished,
                command.ListingStatus,
                command.TitleAr,
                command.TitleEn,
                command.DescriptionAr,
                command.DescriptionEn,
                out listing) is { } listingError)
        {
            return UpdatePropertyErrors.ValidationFailed(listingError);
        }

        return null;
    }
}
