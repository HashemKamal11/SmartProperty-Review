using SmartProperty.Application.PropertyRegistry.GetById;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyRegistry.Archive;

internal static class ArchivePropertyErrors
{
    /// <summary>The single registry not-found error, shared with get-by-id rather than redefined.</summary>
    public static readonly Error NotFound = GetPropertyByIdErrors.NotFound;

    public static readonly Error Archived = new(
        "properties.archived",
        "The property is already archived.",
        ErrorType.Conflict);
}
