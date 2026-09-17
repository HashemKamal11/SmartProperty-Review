#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

/// <summary>
/// Registration request body. Members are nullable so a missing value is reported by Application validation (422)
/// instead of the implicit required-member model validation applied to non-nullable members (400).
/// </summary>
public sealed record RegisterRequest(
    string? Email,
    string? Password,
    string? FirstName,
    string? LastName,
    Guid? WorkspaceId)
{
    // Keeps the plaintext password and personal data out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(RegisterRequest)} {{ {nameof(WorkspaceId)} = {WorkspaceId} }}";
    }
}
