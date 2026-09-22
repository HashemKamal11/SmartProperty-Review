using SmartProperty.Application.Authentication.Logout;
using SmartProperty.Domain.Identity;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authentication;

/// <summary>
/// Exercises the real <see cref="LogoutCommandHandler"/>. Logout is idempotent: every token state ends in
/// success, so the response never reveals whether anything was actually revoked.
/// </summary>
public sealed class LogoutCommandHandlerTests
{
    private const string PresentedToken = "presented-refresh-token";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeRefreshTokenRepository _refreshTokens = new();
    private readonly FakeUserRepository _users = new();
    private readonly FakeTokenProvider _tokenProvider = new(Now.AddDays(7));
    private readonly FakeDateTimeProvider _dateTimeProvider = new(Now);
    private readonly FakeUnitOfWork _unitOfWork = new();

    [Fact]
    public async Task UnknownToken_SucceedsWithoutSaving()
    {
        var result = await CreateHandler().Handle(new LogoutCommand(PresentedToken));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task UnknownToken_LooksTheTokenUpByItsHash()
    {
        await CreateHandler().Handle(new LogoutCommand(PresentedToken));

        Assert.Equal(1, _tokenProvider.HashRefreshTokenCallCount);
        Assert.Equal(FakeTokenProvider.HashOf(PresentedToken), _refreshTokens.LastRequestedTokenHash);
        Assert.NotEqual(PresentedToken, _refreshTokens.LastRequestedTokenHash);
    }

    [Fact]
    public async Task ExpiredToken_SucceedsWithoutSaving()
    {
        SeedPresentedToken(expiresAt: Now);

        var result = await CreateHandler().Handle(new LogoutCommand(PresentedToken));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task AlreadyRevokedToken_SucceedsWithoutSavingAndKeepsTheOriginalRevocationTime()
    {
        var storedToken = SeedPresentedToken();
        var originallyRevokedAt = Now.AddHours(-1);
        storedToken.Revoke(originallyRevokedAt);

        var result = await CreateHandler().Handle(new LogoutCommand(PresentedToken));

        Assert.True(result.IsSuccess);
        Assert.Equal(originallyRevokedAt, storedToken.RevokedAt);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ActiveToken_IsRevokedAndCommittedExactlyOnce()
    {
        var storedToken = SeedPresentedToken();

        var result = await CreateHandler().Handle(new LogoutCommand(PresentedToken));

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, storedToken.RevokedAt);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ActiveToken_IssuesNoReplacementAndRevokesNothingElse()
    {
        var otherToken = new RefreshToken(
            Guid.NewGuid(),
            Guid.NewGuid(),
            FakeTokenProvider.HashOf("another-session-token"),
            Now.AddDays(-1),
            Now.AddDays(6));
        _refreshTokens.Seed(otherToken);
        SeedPresentedToken();

        await CreateHandler().Handle(new LogoutCommand(PresentedToken));

        Assert.Empty(_refreshTokens.Added);
        Assert.Equal(0, _tokenProvider.CreateRefreshTokenCallCount);
        Assert.Equal(0, _tokenProvider.CreateAccessTokenCallCount);
        Assert.Null(otherToken.RevokedAt);
    }

    [Fact]
    public async Task ActiveToken_LoadsNoUserAndChecksNoAccountStatus()
    {
        // Destroying a credential must stay possible for an account that can no longer sign in, so logout
        // deliberately performs no user lookup at all.
        SeedPresentedToken();

        await CreateHandler().Handle(new LogoutCommand(PresentedToken));

        Assert.Equal(0, _users.GetByIdCallCount);
        Assert.Equal(0, _users.GetByEmailCallCount);
    }

    [Fact]
    public async Task RefreshTokenConcurrencyConflict_IsReportedAsSuccess()
    {
        // Application-level mapping only. A real concurrent revocation against PostgreSQL belongs to STEP 05.7B.
        SeedPresentedToken();
        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.RefreshTokenConcurrencyConflict();

        var result = await CreateHandler().Handle(new LogoutCommand(PresentedToken));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task UnrelatedSaveFailure_Propagates()
    {
        SeedPresentedToken();
        _unitOfWork.ExceptionToThrow = new InvalidOperationException("persistence is unavailable");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateHandler().Handle(new LogoutCommand(PresentedToken)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankToken_IsRejectedBeforeAnythingIsHashedOrLookedUp(string refreshToken)
    {
        var result = await CreateHandler().Handle(new LogoutCommand(refreshToken));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal(0, _tokenProvider.HashRefreshTokenCallCount);
        Assert.Equal(0, _refreshTokens.GetByTokenHashCallCount);
    }

    [Fact]
    public async Task NullCommand_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateHandler().Handle(null!));
    }

    private LogoutCommandHandler CreateHandler()
    {
        return new LogoutCommandHandler(_refreshTokens, _tokenProvider, _dateTimeProvider, _unitOfWork);
    }

    private RefreshToken SeedPresentedToken(DateTimeOffset? expiresAt = null)
    {
        var refreshToken = new RefreshToken(
            Guid.NewGuid(),
            Guid.NewGuid(),
            FakeTokenProvider.HashOf(PresentedToken),
            Now.AddDays(-1),
            expiresAt ?? Now.AddDays(6));

        _refreshTokens.Seed(refreshToken);

        return refreshToken;
    }
}
