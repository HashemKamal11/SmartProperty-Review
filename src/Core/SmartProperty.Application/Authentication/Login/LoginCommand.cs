using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Authentication.Login;

public sealed record LoginCommand(
    string Email,
    string Password) : ICommand<LoginResult>
{
    // Keeps the plaintext password and the email out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(LoginCommand)} {{ }}";
    }
}
