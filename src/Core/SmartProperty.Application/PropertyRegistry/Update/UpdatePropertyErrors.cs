using SmartProperty.Application.PropertyRegistry.GetById;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyRegistry.Update;

internal static class UpdatePropertyErrors
{
    /// <summary>The single registry not-found error, shared with get-by-id rather than redefined.</summary>
    public static readonly Error NotFound = GetPropertyByIdErrors.NotFound;

    public static readonly Error Archived = new(
        "properties.archived",
        "An archived property cannot be updated.",
        ErrorType.Conflict);

    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
