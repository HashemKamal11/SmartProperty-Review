using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Authentication.PhoneVerifications;

public sealed record ResendPhoneVerificationCommand(string PhoneNumber) : ICommand
{
    // Keeps the phone number out of logs and debugger displays of the record.
    public override string ToString()
    {
        return nameof(ResendPhoneVerificationCommand);
    }
}
