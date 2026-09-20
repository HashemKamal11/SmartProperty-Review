using SmartProperty.Common.Results;

namespace SmartProperty.Application.Authentication.Refresh;

/// <summary>
/// Input validation for refresh. Returns the first failing rule, or null when the command is valid.
/// The token is an opaque credential, so only presence and a defensive length bound are checked; its format
/// is never validated or decoded here.
/// </summary>
internal static class RefreshCommandValidator
{
    // A defensive bound on input taken into hashing, not a format rule. The tokens issued today are about 86
    // Base64Url characters (64 random bytes), and the token_hash column is fixed at 128 characters regardless of
    // input length, so this only has to reject absurd payloads while leaving room for a longer future token
    // format. The same 512-character bound is used as the request DTO's documented maximum.
    private const int RefreshTokenMaxLength = 512;

    public static Error? Validate(RefreshCommand command)
    {
        // The token is matched exactly against a stored hash, so it is never trimmed. A whitespace-only value
        // cannot be a token and is rejected as missing.
        if (string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            return RefreshErrors.ValidationFailed("Refresh token is required.");
        }

        if (command.RefreshToken.Length > RefreshTokenMaxLength)
        {
            return RefreshErrors.ValidationFailed(
                $"Refresh token must not exceed {RefreshTokenMaxLength} characters.");
        }

        return null;
    }
}
