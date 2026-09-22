using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// Records every refresh token a handler adds so a test can assert what would have been written, in particular
/// that the stored value is a hash and never the raw token handed back to the caller.
/// </summary>
internal sealed class FakeRefreshTokenRepository : IRefreshTokenRepository
{
    private readonly Dictionary<string, RefreshToken> _tokensByHash = [];
    private readonly List<RefreshToken> _added = [];

    public IReadOnlyList<RefreshToken> Added => _added;

    public string? LastRequestedTokenHash { get; private set; }

    public int GetByTokenHashCallCount { get; private set; }

    public void Seed(RefreshToken refreshToken)
    {
        _tokensByHash[refreshToken.TokenHash] = refreshToken;
    }

    public Task<RefreshToken?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        GetByTokenHashCallCount++;
        LastRequestedTokenHash = tokenHash;

        return Task.FromResult(_tokensByHash.GetValueOrDefault(tokenHash));
    }

    public Task AddAsync(RefreshToken refreshToken, CancellationToken cancellationToken = default)
    {
        _added.Add(refreshToken);
        Seed(refreshToken);

        return Task.CompletedTask;
    }
}
