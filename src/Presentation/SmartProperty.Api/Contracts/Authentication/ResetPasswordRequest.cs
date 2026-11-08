#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

/// <summary>
/// Step three of a password reset: the new password, with the <c>resetToken</c> that <c>verify-reset-code</c> returned.
/// Members are nullable so a missing value is reported by Application validation (422) instead of the implicit
/// required-member model validation applied to non-nullable members (400).
/// </summary>
public sealed record ResetPasswordRequest(
    string? Email,
    string? ResetToken,
    string? NewPassword,
    string? ConfirmPassword)
{
    // Keeps the email, the reset token, and the plaintext passwords out of logs and debugger displays.
    public override string ToString()
    {
        return $"{nameof(ResetPasswordRequest)} {{ }}";
    }
}
