using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyMatching.Search;

internal static class SearchPropertiesErrors
{
    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
