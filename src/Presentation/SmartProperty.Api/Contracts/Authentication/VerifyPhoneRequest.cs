#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

/// <summary>
/// Phone verification request body. Members are nullable so a missing value is reported by Application validation
/// (422) instead of the implicit required-member model validation applied to non-nullable members (400).
/// </summary>
public sealed record VerifyPhoneRequest(
    string? PhoneNumber,
    string? Code)
{
    // Keeps the phone number and the one-time code out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(VerifyPhoneRequest)} {{ }}";
    }
}
