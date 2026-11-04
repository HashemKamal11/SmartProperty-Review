using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.Register;

/// <summary>
/// Input validation for registration. Returns the first failing rule, or null when the command is valid.
/// </summary>
internal static class RegisterCommandValidator
{
    // Match the identity.users column lengths so over-long values fail validation instead of the database write.
    internal const int EmailMaxLength = 320;
    private const int NameMaxLength = 100;

    // Shortest accepted password (NIST SP 800-63B minimum for user-chosen secrets). No composition rules.
    internal const int PasswordMinLength = 8;

    // Defensive bound on input passed to the password hasher.
    internal const int PasswordMaxLength = 128;

    public static Error? Validate(RegisterCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Email))
        {
            return RegisterErrors.ValidationFailed("Email is required.");
        }

        // User stores the trimmed email, so length and structure are checked on the trimmed value.
        var email = command.Email.Trim();

        if (email.Length > EmailMaxLength)
        {
            return RegisterErrors.ValidationFailed($"Email must not exceed {EmailMaxLength} characters.");
        }

        if (!HasEmailStructure(email))
        {
            return RegisterErrors.ValidationFailed("Email must be a valid email address.");
        }

        // The password is hashed exactly as sent, so it is not trimmed here.
        if (string.IsNullOrWhiteSpace(command.Password))
        {
            return RegisterErrors.ValidationFailed("Password is required.");
        }

        if (command.Password.Length < PasswordMinLength)
        {
            return RegisterErrors.ValidationFailed($"Password must be at least {PasswordMinLength} characters.");
        }

        if (command.Password.Length > PasswordMaxLength)
        {
            return RegisterErrors.ValidationFailed($"Password must not exceed {PasswordMaxLength} characters.");
        }

        if (ValidateName(command.FirstName, "First name") is { } firstNameError)
        {
            return firstNameError;
        }

        if (ValidateName(command.LastName, "Last name") is { } lastNameError)
        {
            return lastNameError;
        }

        if (string.IsNullOrWhiteSpace(command.PhoneNumber))
        {
            return RegisterErrors.ValidationFailed("Phone number is required.");
        }

        if (!PhoneNumberFormat.TryNormalize(command.PhoneNumber, out _))
        {
            return RegisterErrors.ValidationFailed(
                "Phone number must be in international format, for example +201012345678.");
        }

        return null;
    }

    private static Error? ValidateName(string name, string displayName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return RegisterErrors.ValidationFailed($"{displayName} is required.");
        }

        if (name.Trim().Length > NameMaxLength)
        {
            return RegisterErrors.ValidationFailed($"{displayName} must not exceed {NameMaxLength} characters.");
        }

        return null;
    }

    // Structure only: one '@' between a non-empty local part and domain, and no whitespace or control characters.
    // It does not prove that the address exists or belongs to the caller.
    internal static bool HasEmailStructure(string email)
    {
        var atIndex = email.IndexOf('@');

        return atIndex > 0
            && atIndex == email.LastIndexOf('@')
            && atIndex < email.Length - 1
            && !email.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
    }
}
