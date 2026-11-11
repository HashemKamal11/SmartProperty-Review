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
        out PropertyPhysicalInfo physical)
    {
        finishingStatus = null;
        physical = PropertyPhysicalInfo.Empty;

        if (!PropertyCommandSupport.TryParseName(command.Type, out propertyType))
        {
            return UpdatePropertyErrors.ValidationFailed(
                $"Type must be one of: {PropertyCommandSupport.NamesOf<PropertyType>()}.");
        }

        // The place decides the address: country, city and district come from the area chosen from the locations list.
        if (command.AreaId is null || command.AreaId == Guid.Empty)
        {
            return UpdatePropertyErrors.ValidationFailed("Area is required: choose the property's area from the locations list.");
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

        if (PropertyCommandSupport.ValidatePhysical(
                command.AreaSqm,
                command.Floor,
                command.Furnished,
                command.TimeZoneId,
                out physical) is { } physicalError)
        {
            return UpdatePropertyErrors.ValidationFailed(physicalError);
        }

        return null;
    }
}
