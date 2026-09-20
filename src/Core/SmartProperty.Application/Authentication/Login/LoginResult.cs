namespace SmartProperty.Application.Authentication.Login;

/// <summary>
/// Outcome of a successful login: the raw access and refresh tokens for the client and their expirations.
/// Only the refresh token's hash is persisted.
/// </summary>
public sealed record LoginResult(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt)
{
    // Keeps both token values out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(LoginResult)} {{ {nameof(AccessTokenExpiresAt)} = {AccessTokenExpiresAt:O}, " +
            $"{nameof(RefreshTokenExpiresAt)} = {RefreshTokenExpiresAt:O} }}";
    }
}
