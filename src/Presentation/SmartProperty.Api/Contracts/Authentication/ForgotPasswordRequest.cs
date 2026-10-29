#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

/// <summary>
/// Forgot password request body. The member is nullable so a missing value is reported by Application validation
/// (422) instead of the implicit required-member model validation applied to non-nullable members (400).
/// </summary>
public sealed record ForgotPasswordRequest(string? Email)
{
    // Keeps the email out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(ForgotPasswordRequest)} {{ }}";
    }
}
