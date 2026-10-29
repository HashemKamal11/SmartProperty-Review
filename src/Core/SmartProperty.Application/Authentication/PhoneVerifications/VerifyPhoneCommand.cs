using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Authentication.PhoneVerifications;

public sealed record VerifyPhoneCommand(Guid UserId, string Code) : ICommand
{
    // Keeps the one-time code out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(VerifyPhoneCommand)} {{ {nameof(UserId)} = {UserId} }}";
    }
}
