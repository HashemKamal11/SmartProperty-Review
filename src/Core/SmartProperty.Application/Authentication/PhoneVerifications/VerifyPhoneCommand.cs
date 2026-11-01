using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Authentication.PhoneVerifications;

public sealed record VerifyPhoneCommand(string PhoneNumber, string Code) : ICommand<PhoneVerified>
{
    // Keeps the phone number and the one-time code out of logs and debugger displays of the record.
    public override string ToString()
    {
        return nameof(VerifyPhoneCommand);
    }
}
