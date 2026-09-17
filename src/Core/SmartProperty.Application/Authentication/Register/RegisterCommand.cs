using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Authentication.Register;

public sealed record RegisterCommand(
    string Email,
    string Password,
    string FirstName,
    string LastName,
    Guid WorkspaceId) : ICommand<RegisterResult>
{
    // Keeps the plaintext password and personal data out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(RegisterCommand)} {{ {nameof(WorkspaceId)} = {WorkspaceId} }}";
    }
}
