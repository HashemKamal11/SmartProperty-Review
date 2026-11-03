using SmartProperty.Common.Results;

namespace SmartProperty.Application.Authentication.Register;

/// <summary>
/// The email, password and name rules for an account an administrator creates, which are the registration rules.
/// There is no phone number: the administrator vouches for the account, so a valid placeholder satisfies the
/// registration validator's phone rule without being stored.
/// </summary>
internal static class NewAccountValidator
{
    private const string PlaceholderPhoneNumber = "+201000000000";

    /// <returns>The first failing rule, or null when the account details are valid.</returns>
    public static Error? Validate(string? email, string? password, string? firstName, string? lastName)
    {
        return RegisterCommandValidator.Validate(new RegisterCommand(
            email ?? string.Empty,
            password ?? string.Empty,
            firstName ?? string.Empty,
            lastName ?? string.Empty,
            PlaceholderPhoneNumber));
    }
}
