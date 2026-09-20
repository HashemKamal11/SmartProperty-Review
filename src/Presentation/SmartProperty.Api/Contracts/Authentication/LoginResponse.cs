#nullable enable
namespace SmartProperty.Api.Contracts.Authentication;

public sealed record LoginResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt)
{
    // Keeps both token values out of logs and debugger displays of the record. JSON serialization is unaffected.
    public override string ToString()
    {
        return $"{nameof(LoginResponse)} {{ {nameof(AccessTokenExpiresAt)} = {AccessTokenExpiresAt:O}, " +
            $"{nameof(RefreshTokenExpiresAt)} = {RefreshTokenExpiresAt:O} }}";
    }
}
