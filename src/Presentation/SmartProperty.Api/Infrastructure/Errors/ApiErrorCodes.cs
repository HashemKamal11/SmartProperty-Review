#nullable enable
namespace SmartProperty.Api.Infrastructure.Errors;

internal static class ApiErrorCodes
{
    public const string MalformedRequest = "request.malformed";
    public const string UnexpectedFailure = "server.unexpected_error";

    /// <summary>
    /// Every authentication challenge. Deliberately one code for all of them: which validation rule rejected the
    /// token is not public information.
    /// </summary>
    public const string Unauthorized = "authentication.unauthorized";

    /// <summary>
    /// Authentication succeeded but authorization did not. Distinct from the account-level
    /// <c>authentication.account_unavailable</c>, which means the account itself may not sign in.
    /// </summary>
    public const string Forbidden = "authorization.forbidden";
}
