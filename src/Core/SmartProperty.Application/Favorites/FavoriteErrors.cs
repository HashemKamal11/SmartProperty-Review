using SmartProperty.Application.PropertyRegistry.GetById;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Favorites;

internal static class FavoriteErrors
{
    /// <summary>The single registry not-found error, shared rather than redefined.</summary>
    public static readonly Error PropertyNotFound = GetPropertyByIdErrors.NotFound;

    public static readonly Error UserNotFound = new(
        "users.not_found",
        "The user was not found.",
        ErrorType.NotFound);

    public static readonly Error PropertyArchived = new(
        "favorites.property_archived",
        "An archived property cannot be added to favorites.",
        ErrorType.Conflict);

    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
