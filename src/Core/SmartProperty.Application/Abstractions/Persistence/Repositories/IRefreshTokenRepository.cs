using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns every refresh token of the user that is neither revoked nor expired at <paramref name="now"/>.
    /// </summary>
    Task<IReadOnlyList<RefreshToken>> GetActiveByUserIdAsync(
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task AddAsync(RefreshToken refreshToken, CancellationToken cancellationToken = default);
}
