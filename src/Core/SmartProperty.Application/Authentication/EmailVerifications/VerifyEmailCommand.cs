using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Authentication.EmailVerifications;

public sealed record VerifyEmailCommand(Guid UserId, string Code) : ICommand<EmailVerified>
{
    // Keeps the one-time code out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(VerifyEmailCommand)} {{ {nameof(UserId)} = {UserId} }}";
    }
}
