#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

/// <summary>
/// Login request body. Members are nullable so a missing value is reported by Application validation (422)
/// instead of the implicit required-member model validation applied to non-nullable members (400).
/// </summary>
public sealed record LoginRequest(
    string? Email,
    string? Password)
{
    // Keeps the plaintext password and the email out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(LoginRequest)} {{ }}";
    }
}
