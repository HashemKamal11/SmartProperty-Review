using System.Security.Cryptography;
using System.Text;
using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Time;

namespace SmartProperty.Persistence.IntegrationTests.Infrastructure;

/// <summary>
/// A token provider with no cryptographic configuration, so the race tests stay about the database rather than
/// about JWT signing.
/// </summary>
/// <remarks>
/// It keeps the one property the handlers actually depend on: hashing is deterministic, so the same presented
/// token always resolves to the same stored hash, and two issued tokens never collide. The values it produces
/// are obviously test-only, and the access token is not a JWT — nothing here is ever persisted except the hash.
/// </remarks>
internal sealed class TestTokenProvider(IDateTimeProvider clock) : ITokenProvider
{
    private int issued;

    public AccessToken CreateAccessToken(Guid userId)
    {
        return new AccessToken($"test-access-token-{userId:n}", clock.UtcNow.AddMinutes(15));
    }

    public GeneratedRefreshToken CreateRefreshToken()
    {
        // Unique per call, which is all rotation requires: two racers must not generate the same replacement.
        var value = $"test-refresh-token-{Interlocked.Increment(ref issued)}-{Guid.NewGuid():n}";

        return new GeneratedRefreshToken(value, HashRefreshToken(value), clock.UtcNow.AddDays(7));
    }

    public string HashRefreshToken(string refreshToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        // 64 hexadecimal characters, inside the 128-character token_hash column.
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
    }
}
