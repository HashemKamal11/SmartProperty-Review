using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Create;

internal static class CreatePropertyCommandValidator
{
    public static Error? Validate(
        CreatePropertyCommand command,
        out PropertyType propertyType,
        out FinishingStatus? finishingStatus,
        out PropertyListingInfo listing)
    {
        finishingStatus = null;
        listing = PropertyListingInfo.Empty;

        if (!PropertyCommandSupport.TryParseName(command.Type, out propertyType))
        {
            return CreatePropertyErrors.ValidationFailed(
                $"Type must be one of: {PropertyCommandSupport.NamesOf<PropertyType>()}.");
        }

        if (string.IsNullOrWhiteSpace(command.CountryCode))
        {
            return CreatePropertyErrors.ValidationFailed("Country code is required.");
        }

        var countryCode = command.CountryCode.Trim();
        if (countryCode.Length != 2
            || !char.IsAsciiLetter(countryCode[0])
            || !char.IsAsciiLetter(countryCode[1]))
        {
            return CreatePropertyErrors.ValidationFailed("Country code must contain exactly two ASCII letters.");
        }

        if (command.Latitude is < -90m or > 90m)
        {
            return CreatePropertyErrors.ValidationFailed("Latitude must be between -90 and 90 degrees.");
        }

        if (command.Longitude is < -180m or > 180m)
        {
            return CreatePropertyErrors.ValidationFailed("Longitude must be between -180 and 180 degrees.");
        }

        if (PropertyCommandSupport.ValidateSpecifications(
                command.Bedrooms,
                command.Bathrooms,
                command.BuiltDate,
                command.FinishingStatus,
                out finishingStatus) is { } specificationError)
        {
            return CreatePropertyErrors.ValidationFailed(specificationError);
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
            return CreatePropertyErrors.ValidationFailed(listingError);
        }

        return null;
    }
}
