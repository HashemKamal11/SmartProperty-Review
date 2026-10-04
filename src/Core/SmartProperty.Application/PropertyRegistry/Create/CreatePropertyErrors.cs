using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyRegistry.Create;

internal static class CreatePropertyErrors
{
    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
