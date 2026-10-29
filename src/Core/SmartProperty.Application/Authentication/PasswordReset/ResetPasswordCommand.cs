using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Authentication.PasswordReset;

public sealed record ResetPasswordCommand(string Email, string Code, string NewPassword) : ICommand
{
    // Keeps the email, the one-time code, and the plaintext password out of logs and debugger displays.
    public override string ToString()
    {
        return nameof(ResetPasswordCommand);
    }
}
