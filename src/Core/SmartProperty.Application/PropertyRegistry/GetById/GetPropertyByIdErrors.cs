using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyRegistry.GetById;

internal static class GetPropertyByIdErrors
{
    public static readonly Error NotFound = new(
        "properties.not_found",
        "The property was not found.",
        ErrorType.NotFound);
}
