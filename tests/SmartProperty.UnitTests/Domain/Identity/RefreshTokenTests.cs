using SmartProperty.Domain.Identity;
using Xunit;

namespace SmartProperty.UnitTests.Domain.Identity;

/// <summary>
/// Covers the activity and revocation rules the authentication use cases depend on. Every time value is
/// supplied by the test; nothing here reads a machine clock or measures elapsed time.
/// </summary>
public sealed class RefreshTokenTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiresAt = CreatedAt.AddDays(7);

    [Fact]
    public void IsActive_TrueBeforeExpiration()
    {
        var refreshToken = CreateRefreshToken();

        Assert.True(refreshToken.IsActive(ExpiresAt.AddSeconds(-1)));
    }

    [Fact]
    public void IsActive_FalseExactlyAtExpiration()
    {
        // The boundary is exclusive: a token is active only strictly before ExpiresAt.
        var refreshToken = CreateRefreshToken();

        Assert.False(refreshToken.IsActive(ExpiresAt));
    }

    [Fact]
    public void IsActive_FalseAfterExpiration()
    {
        var refreshToken = CreateRefreshToken();

        Assert.False(refreshToken.IsActive(ExpiresAt.AddSeconds(1)));
    }

    [Fact]
    public void IsActive_FalseOnceRevokedEvenBeforeExpiration()
    {
        var refreshToken = CreateRefreshToken();
        refreshToken.Revoke(CreatedAt.AddHours(1));

        Assert.False(refreshToken.IsActive(CreatedAt.AddHours(2)));
    }

    [Fact]
    public void Revoke_SetsRevokedAt()
    {
        var refreshToken = CreateRefreshToken();
        var revokedAt = CreatedAt.AddHours(1);

        refreshToken.Revoke(revokedAt);

        Assert.Equal(revokedAt, refreshToken.RevokedAt);
    }

    [Fact]
    public void Revoke_RejectsASecondRevocation()
    {
        var refreshToken = CreateRefreshToken();
        refreshToken.Revoke(CreatedAt.AddHours(1));

        Assert.Throws<InvalidOperationException>(() => refreshToken.Revoke(CreatedAt.AddHours(2)));
    }

    [Fact]
    public void Revoke_RejectsATimeBeforeCreation()
    {
        var refreshToken = CreateRefreshToken();

        Assert.Throws<ArgumentException>(() => refreshToken.Revoke(CreatedAt.AddSeconds(-1)));
    }

    [Fact]
    public void Revoke_AllowsRevocationAtTheCreationInstant()
    {
        var refreshToken = CreateRefreshToken();

        refreshToken.Revoke(CreatedAt);

        Assert.Equal(CreatedAt, refreshToken.RevokedAt);
    }

    [Fact]
    public void Constructor_RejectsExpirationEqualToCreation()
    {
        Assert.Throws<ArgumentException>(() =>
            new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), "token-hash", CreatedAt, CreatedAt));
    }

    [Fact]
    public void Constructor_RejectsExpirationBeforeCreation()
    {
        Assert.Throws<ArgumentException>(() =>
            new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), "token-hash", CreatedAt, CreatedAt.AddSeconds(-1)));
    }

    [Fact]
    public void Constructor_RejectsEmptyId()
    {
        Assert.Throws<ArgumentException>(() =>
            new RefreshToken(Guid.Empty, Guid.NewGuid(), "token-hash", CreatedAt, ExpiresAt));
    }

    [Fact]
    public void Constructor_RejectsEmptyUserId()
    {
        Assert.Throws<ArgumentException>(() =>
            new RefreshToken(Guid.NewGuid(), Guid.Empty, "token-hash", CreatedAt, ExpiresAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankTokenHash(string tokenHash)
    {
        Assert.Throws<ArgumentException>(() =>
            new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), tokenHash, CreatedAt, ExpiresAt));
    }

    [Fact]
    public void Constructor_LeavesANewTokenUnrevoked()
    {
        var refreshToken = CreateRefreshToken();

        Assert.Null(refreshToken.RevokedAt);
    }

    private static RefreshToken CreateRefreshToken()
    {
        return new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), "token-hash", CreatedAt, ExpiresAt);
    }
}
