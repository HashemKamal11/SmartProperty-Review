using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Authentication.PasswordReset;

/// <summary>
/// Step three of a password reset: the new password, presented with the reset token that verifying the code returned.
/// </summary>
public sealed record ResetPasswordCommand(
    string Email,
    string ResetToken,
    string NewPassword,
    string ConfirmPassword) : ICommand
{
    // Keeps the email, the reset token, and the plaintext passwords out of logs and debugger displays.
    public override string ToString()
    {
        return nameof(ResetPasswordCommand);
    }
}
