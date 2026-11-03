#nullable enable
namespace SmartProperty.Api.Contracts.Workspaces;

/// <param name="Name">The workspace name.</param>
/// <param name="Admin">Optional account to create and make admin of the new workspace.</param>
public sealed record CreateWorkspaceRequest(string? Name, CreateWorkspaceAdminRequest? Admin = null);

public sealed record CreateWorkspaceAdminRequest(string? Email, string? Password, string? FirstName, string? LastName)
{
    // Keeps the plaintext password out of logs and debugger displays of the record.
    public override string ToString() => nameof(CreateWorkspaceAdminRequest);
}
