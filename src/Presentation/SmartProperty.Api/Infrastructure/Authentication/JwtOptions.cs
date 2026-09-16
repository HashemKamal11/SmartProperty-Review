#nullable enable
using System.Text;

namespace SmartProperty.Api.Infrastructure.Authentication;

/// <summary>
/// JWT and refresh-token settings bound from the "Jwt" configuration section.
/// SigningKey must be supplied through User Secrets, environment variables (Jwt__SigningKey),
/// or secure deployment configuration; it is never committed to source control.
/// </summary>
internal sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    // HMAC-SHA256 requires a key of at least 256 bits.
    public const int MinimumSigningKeyBytes = 32;

    public string Issuer { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string SigningKey { get; init; } = string.Empty;
    public TimeSpan AccessTokenLifetime { get; init; }
    public TimeSpan RefreshTokenLifetime { get; init; }

    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(Issuer)
            && !string.IsNullOrWhiteSpace(Audience)
            && !string.IsNullOrWhiteSpace(SigningKey)
            && Encoding.UTF8.GetByteCount(SigningKey) >= MinimumSigningKeyBytes
            && AccessTokenLifetime > TimeSpan.Zero
            && RefreshTokenLifetime > TimeSpan.Zero;
    }

    /// <summary>
    /// Technical safety check only, not a lifetime policy: expiration dates are computed as
    /// UtcNow + lifetime, which must not exceed <see cref="DateTimeOffset.MaxValue"/>.
    /// </summary>
    public bool HasRepresentableLifetimes(DateTimeOffset utcNow)
    {
        var maximumLifetime = DateTimeOffset.MaxValue - utcNow;

        return AccessTokenLifetime <= maximumLifetime
            && RefreshTokenLifetime <= maximumLifetime;
    }
}
