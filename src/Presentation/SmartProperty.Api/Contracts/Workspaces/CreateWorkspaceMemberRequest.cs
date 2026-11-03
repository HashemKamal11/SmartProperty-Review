#nullable enable
namespace SmartProperty.Api.Contracts.Workspaces;

/// <summary>Members are nullable so a missing value is a validation failure (422), not a binding failure (400).</summary>
public sealed record CreateWorkspaceMemberRequest(string? Email, string? Password, string? FirstName, string? LastName)
{
    // Keeps the plaintext password out of logs and debugger displays of the record.
    public override string ToString() => nameof(CreateWorkspaceMemberRequest);
}

public sealed record WorkspaceMemberResponse(
    Guid UserId,
    Guid MembershipId,
    Guid WorkspaceId,
    string Email,
    string FirstName,
    string LastName,
    string Role,
    DateTimeOffset CreatedAt);
