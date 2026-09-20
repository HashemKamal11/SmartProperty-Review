using SmartProperty.Common.Results;

namespace SmartProperty.Application.Authentication.Me;

internal static class MeErrors
{
    // Returned when the request carries no usable subject and when the subject names a user that no longer
    // exists, so the response never confirms whether an account id was ever real. Matches the code and message
    // the JWT Bearer challenge produces, so an expired token and a deleted user look identical to a client.
    public static readonly Error Unauthorized = new(
        "authentication.unauthorized",
        "Authentication is required.",
        ErrorType.Unauthorized);

    // Returned only after the user was found. One error for every non-active status, so the exact status is not
    // disclosed. Deliberately the same account-level error Login and Refresh return, and deliberately distinct
    // from the authorization failure that a missing permission produces.
    public static readonly Error AccountUnavailable = new(
        "authentication.account_unavailable",
        "This account is not currently allowed to sign in.",
        ErrorType.Forbidden);
}
