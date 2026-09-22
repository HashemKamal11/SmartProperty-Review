using Microsoft.Extensions.Options;
using SmartProperty.Api.Infrastructure.Authentication;
using SmartProperty.Application.Abstractions.Time;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Issues structurally correct access tokens signed with a key the test host does not trust, so a test can
/// prove the host rejects a token it did not sign rather than merely one that is malformed.
/// </summary>
internal static class ForeignTokenIssuer
{
    /// <summary>
    /// TEST-ONLY, NON-SECRET key, and deliberately not <see cref="TestJwt.SigningKey"/>. It signs nothing the
    /// host will ever accept.
    /// </summary>
    private const string SigningKey = "smartproperty-test-only-untrusted-key-not-a-secret-do-not-deploy";

    public static string CreateAccessToken(Guid userId)
    {
        var options = Options.Create(new JwtOptions
        {
            Issuer = TestJwt.Issuer,
            Audience = TestJwt.Audience,
            SigningKey = SigningKey,
            AccessTokenLifetime = TestJwt.AccessTokenLifetime,
            RefreshTokenLifetime = TestJwt.RefreshTokenLifetime
        });

        var tokenProvider = new TokenProvider(options, new NowDateTimeProvider());

        return tokenProvider.CreateAccessToken(userId).Value;
    }

    private sealed class NowDateTimeProvider : IDateTimeProvider
    {
        // The token must be live so the test proves the signature was rejected, not the lifetime.
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }
}
