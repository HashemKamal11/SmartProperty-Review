#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

/// <summary>
/// Reset password request body. Members are nullable so a missing value is reported by Application validation
/// (422) instead of the implicit required-member model validation applied to non-nullable members (400).
/// </summary>
public sealed record ResetPasswordRequest(
    string? Email,
    string? Code,
    string? NewPassword)
{
    // Keeps the email, the one-time code, and the plaintext password out of logs and debugger displays.
    public override string ToString()
    {
        return $"{nameof(ResetPasswordRequest)} {{ }}";
    }
}
