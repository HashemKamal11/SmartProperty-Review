namespace SmartProperty.Application.Authentication.Refresh;

/// <summary>
/// Outcome of a successful refresh: a new access token and the new raw refresh token that replaces the rotated one.
/// Only the new refresh token's hash is persisted.
/// </summary>
public sealed record RefreshResult(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt)
{
    // Keeps both token values out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(RefreshResult)} {{ {nameof(AccessTokenExpiresAt)} = {AccessTokenExpiresAt:O}, " +
            $"{nameof(RefreshTokenExpiresAt)} = {RefreshTokenExpiresAt:O} }}";
    }
}
