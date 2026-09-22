using System.Security.Cryptography;
using System.Text;
using SmartProperty.Application.Abstractions.Authentication;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// Issues predictable tokens so a test can tell a raw token from its hash by value. Every generated refresh
/// token gets a distinct sequence number, which is what lets a rotation test prove the replacement token is a
/// new one rather than the presented token written back.
/// </summary>
internal sealed class FakeTokenProvider(DateTimeOffset refreshTokenExpiresAt) : ITokenProvider
{
    public const string RawTokenPrefix = "raw-refresh-token-";
    public const string TokenHashPrefix = "hash-of-";
    public const string AccessTokenPrefix = "access-token-for-";

    private int _refreshTokenCount;

    public DateTimeOffset AccessTokenExpiresAt { get; set; } = refreshTokenExpiresAt;

    public DateTimeOffset RefreshTokenExpiresAt { get; set; } = refreshTokenExpiresAt;

    public int CreateAccessTokenCallCount { get; private set; }

    public int CreateRefreshTokenCallCount { get; private set; }

    public int HashRefreshTokenCallCount { get; private set; }

    public string? LastHashedRefreshToken { get; private set; }

    public List<GeneratedRefreshToken> GeneratedRefreshTokens { get; } = [];

    public AccessToken CreateAccessToken(Guid userId)
    {
        CreateAccessTokenCallCount++;

        return new AccessToken($"{AccessTokenPrefix}{userId}", AccessTokenExpiresAt);
    }

    public GeneratedRefreshToken CreateRefreshToken()
    {
        CreateRefreshTokenCallCount++;

        var rawToken = $"{RawTokenPrefix}{++_refreshTokenCount}";
        var generated = new GeneratedRefreshToken(rawToken, HashOf(rawToken), RefreshTokenExpiresAt);

        GeneratedRefreshTokens.Add(generated);

        return generated;
    }

    public string HashRefreshToken(string refreshToken)
    {
        HashRefreshTokenCallCount++;
        LastHashedRefreshToken = refreshToken;

        return HashOf(refreshToken);
    }

    /// <summary>
    /// The hash a raw token maps to, so a test can seed a stored token for a raw value it chose. Deliberately
    /// does not embed the raw token: a test asserting that only a hash was persisted would otherwise pass
    /// against a fake that had in fact leaked the credential.
    /// </summary>
    public static string HashOf(string refreshToken)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));

        return $"{TokenHashPrefix}{Convert.ToHexStringLower(digest)}";
    }
}
