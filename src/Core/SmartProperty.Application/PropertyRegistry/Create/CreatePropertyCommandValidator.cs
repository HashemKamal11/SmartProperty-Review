using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Create;

internal static class CreatePropertyCommandValidator
{
    public static Error? Validate(CreatePropertyCommand command, out PropertyType propertyType)
    {
        propertyType = default;

        propertyType = command.Type switch
        {
            nameof(PropertyType.Land) => PropertyType.Land,
            nameof(PropertyType.Building) => PropertyType.Building,
            nameof(PropertyType.Unit) => PropertyType.Unit,
            _ => default
        };

        if (propertyType == default)
        {
            return CreatePropertyErrors.ValidationFailed("Type must be Land, Building, or Unit.");
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

        return null;
    }
}
