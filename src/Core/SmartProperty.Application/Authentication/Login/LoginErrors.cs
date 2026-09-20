using SmartProperty.Common.Results;

namespace SmartProperty.Application.Authentication.Login;

internal static class LoginErrors
{
    // Returned for an unknown email, a missing credential, and a password that does not verify, so the response
    // never reveals which of them occurred.
    public static readonly Error InvalidCredentials = new(
        "authentication.invalid_credentials",
        "Invalid email or password.",
        ErrorType.Unauthorized);

    // Returned only after the password verified. One error for every non-active status, so the exact status
    // is not disclosed.
    public static readonly Error AccountUnavailable = new(
        "authentication.account_unavailable",
        "This account is not currently allowed to sign in.",
        ErrorType.Forbidden);

    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
