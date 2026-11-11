using SmartProperty.Common.Results;
using SmartProperty.Domain.PropertyRegistry;

namespace SmartProperty.Application.PropertyRegistry.Create;

internal static class CreatePropertyCommandValidator
{
    public static Error? Validate(
        CreatePropertyCommand command,
        out PropertyType propertyType,
        out FinishingStatus? finishingStatus,
        out PropertyPhysicalInfo physical)
    {
        finishingStatus = null;
        physical = PropertyPhysicalInfo.Empty;

        if (!PropertyCommandSupport.TryParseName(command.Type, out propertyType))
        {
            return CreatePropertyErrors.ValidationFailed(
                $"Type must be one of: {PropertyCommandSupport.NamesOf<PropertyType>()}.");
        }

        // The place decides the address: country, city and district come from the area chosen from the locations list.
        if (command.AreaId is null || command.AreaId == Guid.Empty)
        {
            return CreatePropertyErrors.ValidationFailed("Area is required: choose the property's area from the locations list.");
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

        if (PropertyCommandSupport.ValidatePhysical(
                command.AreaSqm,
                command.Floor,
                command.Furnished,
                command.TimeZoneId,
                out physical) is { } physicalError)
        {
            return CreatePropertyErrors.ValidationFailed(physicalError);
        }

        return null;
    }
}
