using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Authentication.EmailVerifications;

public sealed record ResendEmailVerificationCommand(string Email) : ICommand
{
    // Keeps the email out of logs and debugger displays of the record.
    public override string ToString()
    {
        return nameof(ResendEmailVerificationCommand);
    }
}
