using SmartProperty.Application.PropertyRegistry.GetById;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyMatching.Record;

internal static class RecordPropertyMatchErrors
{
    public static readonly Error PropertyNotFound = GetPropertyByIdErrors.NotFound;

    public static readonly Error UserNotFound = new(
        "users.not_found",
        "The user was not found.",
        ErrorType.NotFound);

    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
