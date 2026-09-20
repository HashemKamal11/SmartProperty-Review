using SmartProperty.Common.Results;

namespace SmartProperty.Application.Authentication.Login;

/// <summary>
/// Input validation for login. Returns the first failing rule, or null when the command is valid.
/// Uses the same email and password bounds as registration, so any registered email passes.
/// </summary>
internal static class LoginCommandValidator
{
    // Matches the identity.users email column length.
    private const int EmailMaxLength = 320;

    // Defensive bound on input passed to the password hasher. Not a strength policy; none is approved yet.
    private const int PasswordMaxLength = 128;

    public static Error? Validate(LoginCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Email))
        {
            return LoginErrors.ValidationFailed("Email is required.");
        }

        // Emails are stored trimmed, so length and structure are checked on the trimmed value.
        var email = command.Email.Trim();

        if (email.Length > EmailMaxLength)
        {
            return LoginErrors.ValidationFailed($"Email must not exceed {EmailMaxLength} characters.");
        }

        if (!HasEmailStructure(email))
        {
            return LoginErrors.ValidationFailed("Email must be a valid email address.");
        }

        // The password is verified exactly as sent, so it is not trimmed here.
        if (string.IsNullOrWhiteSpace(command.Password))
        {
            return LoginErrors.ValidationFailed("Password is required.");
        }

        if (command.Password.Length > PasswordMaxLength)
        {
            return LoginErrors.ValidationFailed($"Password must not exceed {PasswordMaxLength} characters.");
        }

        return null;
    }

    // Same structural rule as registration: one '@' between a non-empty local part and domain, and no whitespace
    // or control characters.
    private static bool HasEmailStructure(string email)
    {
        var atIndex = email.IndexOf('@');

        return atIndex > 0
            && atIndex == email.LastIndexOf('@')
            && atIndex < email.Length - 1
            && !email.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
    }
}
