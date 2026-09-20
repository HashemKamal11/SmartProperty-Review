using SmartProperty.Common.Results;

namespace SmartProperty.Application.Authentication.Refresh;

internal static class RefreshErrors
{
    // Returned for an unknown token hash, a revoked token, an expired token, a token whose user no longer exists,
    // and the loser of a concurrent rotation, so the response never reveals which of them occurred.
    public static readonly Error InvalidRefreshToken = new(
        "authentication.invalid_refresh_token",
        "Invalid or expired refresh token.",
        ErrorType.Unauthorized);

    // Returned only after the refresh token itself verified. One error for every non-active status, so the exact
    // status is not disclosed. Mirrors the login wording deliberately: both mean "this account may not sign in".
    public static readonly Error AccountUnavailable = new(
        "authentication.account_unavailable",
        "This account is not currently allowed to sign in.",
        ErrorType.Forbidden);

    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
