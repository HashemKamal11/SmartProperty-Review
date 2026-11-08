using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Authentication.PasswordReset;

/// <summary>
/// Step two of a password reset: the six-digit code the user was sent is checked on its own, before they choose a new
/// password. A correct code is spent and exchanged for a short-lived <see cref="VerifyResetCodeResult.ResetToken"/>.
/// </summary>
public sealed record VerifyResetCodeCommand(string Email, string Code) : ICommand<VerifyResetCodeResult>
{
    // Keeps the email and the one-time code out of logs and debugger displays.
    public override string ToString()
    {
        return nameof(VerifyResetCodeCommand);
    }
}

/// <param name="ResetToken">What the new-password step must present. Single use.</param>
/// <param name="ExpiresAt">When the token stops working; the user then starts again from their email.</param>
public sealed record VerifyResetCodeResult(string ResetToken, DateTimeOffset ExpiresAt)
{
    // The token is a credential for changing the password, so it never reaches a log.
    public override string ToString()
    {
        return nameof(VerifyResetCodeResult);
    }
}
