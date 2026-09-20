using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Authentication.Logout;

/// <summary>
/// Revokes the presented refresh token. Returns no value: the caller learns nothing about the token's state.
/// </summary>
public sealed record LogoutCommand(
    string RefreshToken) : ICommand
{
    // Keeps the raw refresh token, which is a credential, out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(LogoutCommand)} {{ }}";
    }
}
