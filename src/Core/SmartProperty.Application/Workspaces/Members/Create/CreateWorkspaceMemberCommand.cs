using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Workspaces.Members.Create;

/// <summary>An administrator of a workspace creating a member account in it.</summary>
/// <param name="AdminUserId">The signed-in caller. The workspace is not named by the request: it is the one in which
/// this user holds <c>workspace.members.create</c>.</param>
public sealed record CreateWorkspaceMemberCommand(
    Guid AdminUserId,
    string? Email,
    string? Password,
    string? FirstName,
    string? LastName) : ICommand<CreateWorkspaceMemberResult>
{
    // Keeps the plaintext password and personal data out of logs and debugger displays of the record.
    public override string ToString() => $"{nameof(CreateWorkspaceMemberCommand)} {{ {nameof(AdminUserId)} = {AdminUserId} }}";
}

public sealed record CreateWorkspaceMemberResult(
    Guid UserId,
    Guid MembershipId,
    Guid WorkspaceId,
    string Email,
    string FirstName,
    string LastName,
    string RoleName,
    DateTimeOffset CreatedAt);
