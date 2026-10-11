using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyRegistry.List;

internal static class GetPropertiesErrors
{
    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
