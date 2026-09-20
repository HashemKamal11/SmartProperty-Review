#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

/// <summary>
/// Refresh request body. The member is nullable so a missing value is reported by Application validation (422)
/// instead of the implicit required-member model validation applied to non-nullable members (400).
/// </summary>
public sealed record RefreshRequest(
    string? RefreshToken)
{
    // Keeps the raw refresh token, which is a credential, out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(RefreshRequest)} {{ }}";
    }
}
