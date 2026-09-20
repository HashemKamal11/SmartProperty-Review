using SmartProperty.Common.Results;

namespace SmartProperty.Application.Authentication.Logout;

/// <summary>
/// Input validation for logout. Returns the first failing rule, or null when the command is valid.
/// The token is an opaque credential, so only presence and a defensive length bound are checked; its format
/// is never validated or decoded here.
/// </summary>
internal static class LogoutCommandValidator
{
    // The same defensive bound Refresh applies to the same input. It is a limit on input taken into hashing,
    // not a format rule: it only has to reject absurd payloads while leaving room for a future token format.
    // Refresh keeps its own copy because no shared constant exists; introducing one would mean refactoring
    // Refresh, which this step does not do.
    private const int RefreshTokenMaxLength = 512;

    public static Error? Validate(LogoutCommand command)
    {
        // The token is matched exactly against a stored hash, so it is never trimmed. A whitespace-only value
        // cannot be a token and is rejected as missing.
        if (string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            return ValidationFailed("Refresh token is required.");
        }

        if (command.RefreshToken.Length > RefreshTokenMaxLength)
        {
            return ValidationFailed($"Refresh token must not exceed {RefreshTokenMaxLength} characters.");
        }

        return null;
    }

    // Logout produces no error of its own: every token state ends in success, so the only failure it can report
    // is the generic validation one. It is built here rather than in a LogoutErrors class holding nothing else.
    private static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
