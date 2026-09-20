namespace SmartProperty.Application.Abstractions.Authentication;

/// <summary>
/// Issues authentication tokens. Does not look up users or decide whether a user may authenticate;
/// callers must enforce user status before requesting tokens.
/// </summary>
public interface ITokenProvider
{
    AccessToken CreateAccessToken(Guid userId);

    /// <summary>
    /// Generates a new opaque refresh token. Only <see cref="GeneratedRefreshToken.TokenHash"/> may be persisted.
    /// </summary>
    GeneratedRefreshToken CreateRefreshToken();

    /// <summary>
    /// Computes the deterministic lookup hash of a raw refresh token presented by a client.
    /// </summary>
    string HashRefreshToken(string refreshToken);
}

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt)
{
    // Keeps the signed token out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(AccessToken)} {{ {nameof(ExpiresAt)} = {ExpiresAt:O} }}";
    }
}

public sealed record GeneratedRefreshToken(string Value, string TokenHash, DateTimeOffset ExpiresAt)
{
    // Keeps the raw token out of logs and debugger displays of the record.
    public override string ToString()
    {
        return $"{nameof(GeneratedRefreshToken)} {{ {nameof(ExpiresAt)} = {ExpiresAt:O} }}";
    }
}
