using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Workspaces.Administration.Create;

/// <param name="Name">The workspace name.</param>
/// <param name="Admin">Optional. When present, a new active account is created and made this workspace's admin.</param>
public sealed record CreateWorkspaceCommand(string? Name, CreateWorkspaceAdmin? Admin = null)
    : ICommand<CreateWorkspaceResult>;

public sealed record CreateWorkspaceAdmin(string? Email, string? Password, string? FirstName, string? LastName)
{
    // Keeps the plaintext password and personal data out of logs and debugger displays of the record.
    public override string ToString() => nameof(CreateWorkspaceAdmin);
}
