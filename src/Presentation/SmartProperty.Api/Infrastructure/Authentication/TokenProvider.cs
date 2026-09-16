#nullable enable
using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Time;

namespace SmartProperty.Api.Infrastructure.Authentication;

internal sealed class TokenProvider(IOptions<JwtOptions> options, IDateTimeProvider dateTimeProvider) : ITokenProvider
{
    // 512 bits of CSPRNG output for opaque refresh tokens.
    private const int RefreshTokenByteLength = 64;

    private readonly JsonWebTokenHandler _tokenHandler = new();

    public AccessToken CreateAccessToken(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        var jwtOptions = options.Value;
        var issuedAt = dateTimeProvider.UtcNow;
        var expiresAt = issuedAt.Add(jwtOptions.AccessTokenLifetime);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwtOptions.Issuer,
            Audience = jwtOptions.Audience,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            ]),
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                CreateSigningKey(jwtOptions.SigningKey),
                SecurityAlgorithms.HmacSha256)
        };

        return new AccessToken(_tokenHandler.CreateToken(descriptor), expiresAt);
    }

    public GeneratedRefreshToken CreateRefreshToken()
    {
        var refreshToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RefreshTokenByteLength));
        var expiresAt = dateTimeProvider.UtcNow.Add(options.Value.RefreshTokenLifetime);

        return new GeneratedRefreshToken(refreshToken, HashRefreshToken(refreshToken), expiresAt);
    }

    public string HashRefreshToken(string refreshToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
    }

    internal static SymmetricSecurityKey CreateSigningKey(string signingKey)
    {
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
    }
}
