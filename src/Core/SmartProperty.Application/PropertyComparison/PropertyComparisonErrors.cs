using SmartProperty.Application.PropertyRegistry.GetById;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.PropertyComparison;

internal static class PropertyComparisonErrors
{
    /// <summary>The single registry not-found error, shared rather than redefined.</summary>
    public static readonly Error PropertyNotFound = GetPropertyByIdErrors.NotFound;

    public static readonly Error UserNotFound = new(
        "users.not_found",
        "The user was not found.",
        ErrorType.NotFound);

    public static readonly Error PropertyArchived = new(
        "comparison.property_archived",
        "An archived property cannot be added to the comparison.",
        ErrorType.Conflict);

    public static readonly Error LimitReached = new(
        "comparison.limit_reached",
        "The comparison already holds the most properties it can. Remove one to add another.",
        ErrorType.Conflict);

    public static readonly Error NotForSaleOrRent = new(
        "comparison.property_not_listed",
        "A property can be compared only once it is listed for sale or for rent.",
        ErrorType.Conflict);

    public static readonly Error TransactionTypeMismatch = new(
        "comparison.transaction_type_mismatch",
        "Only properties for sale can be compared with each other, and likewise those for rent.",
        ErrorType.Conflict);

    public static readonly Error ConcurrentChange = new(
        "comparison.concurrent_change",
        "The comparison was changed by another request. Try again.",
        ErrorType.Conflict);

    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
