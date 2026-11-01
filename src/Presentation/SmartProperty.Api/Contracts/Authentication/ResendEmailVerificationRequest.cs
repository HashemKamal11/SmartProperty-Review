#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

public sealed record ResendEmailVerificationRequest(string? Email)
{
    // Keeps the email out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(ResendEmailVerificationRequest)} {{ }}";
    }
}
