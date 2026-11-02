using SmartProperty.Application.PropertyRegistry.GetById;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyImages;

internal static class PropertyImageErrors
{
    /// <summary>The single registry not-found error, shared rather than redefined.</summary>
    public static readonly Error PropertyNotFound = GetPropertyByIdErrors.NotFound;

    public static readonly Error ImageNotFound = new(
        "property_images.not_found",
        "The image was not found.",
        ErrorType.NotFound);

    /// <summary>An archived property is retired, so its photos are frozen like the rest of its details.</summary>
    public static readonly Error PropertyArchived = new(
        "properties.archived",
        "The property is archived, so its images cannot be changed.",
        ErrorType.Conflict);

    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
