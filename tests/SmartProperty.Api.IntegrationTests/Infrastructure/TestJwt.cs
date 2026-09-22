using Microsoft.Extensions.Options;
using SmartProperty.Api.Infrastructure.Authentication;
using SmartProperty.Application.Abstractions.Time;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// The JWT settings the test host runs on, and the only place a test access token is issued.
/// </summary>
/// <remarks>
/// Everything here is self-contained: no User Secrets, no appsettings.Local, no environment variable, and no
/// developer machine state is read. The signing key below is a throwaway value that exists only to sign tokens
/// this test host will validate in the same process; it is not, and must never become, a deployed key.
///
/// Tokens are issued through the production <see cref="TokenProvider"/> rather than by hand, so the baseline
/// tests assert the claims the API actually emits.
/// </remarks>
internal static class TestJwt
{
    public const string Issuer = "SmartProperty.Tests";
    public const string Audience = "SmartProperty.Api.Tests";

    /// <summary>
    /// TEST-ONLY, NON-SECRET signing key. Committed deliberately so the suite is reproducible anywhere; it
    /// signs nothing outside these tests. Long enough to satisfy the 256-bit HMAC-SHA256 minimum.
    /// </summary>
    public const string SigningKey = "smartproperty-test-only-signing-key-not-a-secret-do-not-deploy";

    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);

    /// <summary>The configuration the test host binds its <c>Jwt</c> section from.</summary>
    public static IEnumerable<KeyValuePair<string, string?>> Configuration()
    {
        return
        [
            new("Jwt:Issuer", Issuer),
            new("Jwt:Audience", Audience),
            new("Jwt:SigningKey", SigningKey),
            new("Jwt:AccessTokenLifetime", AccessTokenLifetime.ToString()),
            new("Jwt:RefreshTokenLifetime", RefreshTokenLifetime.ToString())
        ];
    }

    /// <summary>A token that is currently valid for the test host.</summary>
    public static string CreateAccessToken(Guid userId)
    {
        // Anchored to the current instant because the token has to be live while the request runs. The
        // assertions that follow are about the token's claims and the host's answer, never about elapsed time.
        return CreateAccessToken(userId, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// A token that expired well before now. The gap is far larger than the host's 30-second clock skew, so
    /// the outcome does not depend on how fast the test runs.
    /// </summary>
    public static string CreateExpiredAccessToken(Guid userId)
    {
        return CreateAccessToken(userId, DateTimeOffset.UtcNow.AddHours(-2));
    }

    private static string CreateAccessToken(Guid userId, DateTimeOffset issuedAt)
    {
        var options = Options.Create(new JwtOptions
        {
            Issuer = Issuer,
            Audience = Audience,
            SigningKey = SigningKey,
            AccessTokenLifetime = AccessTokenLifetime,
            RefreshTokenLifetime = RefreshTokenLifetime
        });

        var tokenProvider = new TokenProvider(options, new FixedDateTimeProvider(issuedAt));

        return tokenProvider.CreateAccessToken(userId).Value;
    }

    private sealed class FixedDateTimeProvider(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
