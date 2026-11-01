#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

public sealed record ResendPhoneVerificationRequest(string? PhoneNumber)
{
    // Keeps the phone number out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(ResendPhoneVerificationRequest)} {{ }}";
    }
}
