using SmartProperty.Application.Abstractions.Messaging;

namespace SmartProperty.Application.Authentication.Refresh;

public sealed record RefreshCommand(
    string RefreshToken) : ICommand<RefreshResult>
{
    // Keeps the raw refresh token, which is a credential, out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(RefreshCommand)} {{ }}";
    }
}
