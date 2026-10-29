using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Identity;

internal sealed class RefreshTokenRepository(ApplicationDbContext dbContext) : IRefreshTokenRepository
{
    public Task<RefreshToken?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            throw new ArgumentException("Token hash must not be empty.", nameof(tokenHash));
        }

        return dbContext.RefreshTokens.FirstOrDefaultAsync(
            refreshToken => refreshToken.TokenHash == tokenHash,
            cancellationToken);
    }

    public async Task<IReadOnlyList<RefreshToken>> GetActiveByUserIdAsync(
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id must not be empty.", nameof(userId));
        }

        return await dbContext.RefreshTokens
            .Where(refreshToken => refreshToken.UserId == userId
                && refreshToken.RevokedAt == null
                && refreshToken.ExpiresAt > now)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(RefreshToken refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);

        await dbContext.RefreshTokens.AddAsync(refreshToken, cancellationToken);
    }
}
