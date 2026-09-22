using SmartProperty.Application.Authentication.Refresh;
using SmartProperty.Domain.Identity;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authentication;

/// <summary>
/// Exercises the real <see cref="RefreshCommandHandler"/> against test doubles.
/// </summary>
public sealed class RefreshCommandHandlerTests
{
    private const string PresentedToken = "presented-refresh-token";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeRefreshTokenRepository _refreshTokens = new();
    private readonly FakeUserRepository _users = new();
    private readonly FakeTokenProvider _tokenProvider = new(Now.AddDays(7));
    private readonly FakeDateTimeProvider _dateTimeProvider = new(Now);
    private readonly FakeUnitOfWork _unitOfWork = new();

    private User? _seededUser;

    [Fact]
    public async Task UnknownToken_ReturnsInvalidRefreshToken()
    {
        var result = await CreateHandler().Handle(new RefreshCommand(PresentedToken));

        Assert.True(result.IsFailure);
        Assert.Same(RefreshErrors.InvalidRefreshToken, result.Error);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task UnknownToken_LooksTheTokenUpByItsHashAndNeverByItsRawValue()
    {
        await CreateHandler().Handle(new RefreshCommand(PresentedToken));

        Assert.Equal(1, _tokenProvider.HashRefreshTokenCallCount);
        Assert.Equal(PresentedToken, _tokenProvider.LastHashedRefreshToken);
        Assert.Equal(1, _refreshTokens.GetByTokenHashCallCount);
        Assert.Equal(FakeTokenProvider.HashOf(PresentedToken), _refreshTokens.LastRequestedTokenHash);
        Assert.NotEqual(PresentedToken, _refreshTokens.LastRequestedTokenHash);
    }

    [Fact]
    public async Task ExpiredToken_ReturnsInvalidRefreshToken()
    {
        SeedUser(UserStatus.Active);
        SeedPresentedToken(expiresAt: Now);

        var result = await CreateHandler().Handle(new RefreshCommand(PresentedToken));

        Assert.True(result.IsFailure);
        Assert.Same(RefreshErrors.InvalidRefreshToken, result.Error);
        AssertNothingWasIssuedOrSaved();
    }

    [Fact]
    public async Task RevokedToken_ReturnsInvalidRefreshToken()
    {
        SeedUser(UserStatus.Active);
        var storedToken = SeedPresentedToken();
        storedToken.Revoke(Now.AddMinutes(-1));

        var result = await CreateHandler().Handle(new RefreshCommand(PresentedToken));

        Assert.True(result.IsFailure);
        Assert.Same(RefreshErrors.InvalidRefreshToken, result.Error);
        AssertNothingWasIssuedOrSaved();
    }

    [Fact]
    public async Task MissingUser_ReturnsInvalidRefreshToken()
    {
        // A token whose user no longer exists is invalid session state, not a distinguishable case.
        SeedPresentedToken();

        var result = await CreateHandler().Handle(new RefreshCommand(PresentedToken));

        Assert.True(result.IsFailure);
        Assert.Same(RefreshErrors.InvalidRefreshToken, result.Error);
        AssertNothingWasIssuedOrSaved();
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task NonActiveUser_ReturnsAccountUnavailable(UserStatus status)
    {
        SeedUser(status);
        SeedPresentedToken();

        var result = await CreateHandler().Handle(new RefreshCommand(PresentedToken));

        Assert.True(result.IsFailure);
        Assert.Same(RefreshErrors.AccountUnavailable, result.Error);
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task NonActiveUser_LeavesThePresentedTokenUsable(UserStatus status)
    {
        SeedUser(status);
        var storedToken = SeedPresentedToken();

        await CreateHandler().Handle(new RefreshCommand(PresentedToken));

        Assert.Null(storedToken.RevokedAt);
        AssertNothingWasIssuedOrSaved();
    }

    [Fact]
    public async Task Success_RevokesThePresentedTokenAndIssuesAReplacement()
    {
        var user = SeedUser(UserStatus.Active);
        var storedToken = SeedPresentedToken();

        var result = await CreateHandler().Handle(new RefreshCommand(PresentedToken));

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, storedToken.RevokedAt);

        var replacement = Assert.Single(_refreshTokens.Added);
        Assert.Equal(user.Id, replacement.UserId);
        Assert.NotEqual(storedToken.Id, replacement.Id);
        Assert.Null(replacement.RevokedAt);
    }

    [Fact]
    public async Task Success_ReturnsANewAccessTokenAndANewRawRefreshToken()
    {
        var user = SeedUser(UserStatus.Active);
        SeedPresentedToken();

        var result = await CreateHandler().Handle(new RefreshCommand(PresentedToken));

        var generated = Assert.Single(_tokenProvider.GeneratedRefreshTokens);
        Assert.Equal(1, _tokenProvider.CreateAccessTokenCallCount);
        Assert.Equal($"{FakeTokenProvider.AccessTokenPrefix}{user.Id}", result.Value.AccessToken);
        Assert.Equal(generated.Value, result.Value.RefreshToken);
        Assert.NotEqual(PresentedToken, result.Value.RefreshToken);
    }

    [Fact]
    public async Task Success_PersistsOnlyTheReplacementTokenHash()
    {
        SeedUser(UserStatus.Active);
        SeedPresentedToken();

        var result = await CreateHandler().Handle(new RefreshCommand(PresentedToken));

        var replacement = Assert.Single(_refreshTokens.Added);
        var generated = Assert.Single(_tokenProvider.GeneratedRefreshTokens);

        Assert.Equal(generated.TokenHash, replacement.TokenHash);
        Assert.NotEqual(generated.Value, replacement.TokenHash);
        Assert.DoesNotContain(result.Value.RefreshToken, replacement.TokenHash, StringComparison.Ordinal);
        Assert.Equal(Now, replacement.CreatedAt);
        Assert.Equal(generated.ExpiresAt, replacement.ExpiresAt);
    }

    [Fact]
    public async Task Success_CommitsExactlyOnce()
    {
        SeedUser(UserStatus.Active);
        SeedPresentedToken();

        await CreateHandler().Handle(new RefreshCommand(PresentedToken));

        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task RefreshTokenConcurrencyConflict_IsMappedToInvalidRefreshToken()
    {
        // Application-level mapping only: the conflict is raised by the fake persistence boundary, so this
        // proves the handler's catch and its public error, not real PostgreSQL race behaviour. A genuine
        // same-token race against the database belongs to STEP 05.7B.
        SeedUser(UserStatus.Active);
        SeedPresentedToken();
        _unitOfWork.ExceptionToThrow = FakeUnitOfWork.RefreshTokenConcurrencyConflict();

        var result = await CreateHandler().Handle(new RefreshCommand(PresentedToken));

        Assert.True(result.IsFailure);
        Assert.Same(RefreshErrors.InvalidRefreshToken, result.Error);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task UnrelatedSaveFailure_Propagates()
    {
        // Only a refresh-token concurrency conflict is an expected outcome; anything else must surface.
        SeedUser(UserStatus.Active);
        SeedPresentedToken();
        _unitOfWork.ExceptionToThrow = new InvalidOperationException("persistence is unavailable");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateHandler().Handle(new RefreshCommand(PresentedToken)));

        Assert.Equal("persistence is unavailable", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankToken_IsRejectedBeforeAnythingIsHashedOrLookedUp(string refreshToken)
    {
        var result = await CreateHandler().Handle(new RefreshCommand(refreshToken));

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

    private RefreshCommandHandler CreateHandler()
    {
        return new RefreshCommandHandler(
            _refreshTokens,
            _users,
            _tokenProvider,
            _dateTimeProvider,
            _unitOfWork);
    }

    private User SeedUser(UserStatus status)
    {
        var user = new User(Guid.NewGuid(), "owner@example.com", "Owner", "Example", Now.AddDays(-30));

        switch (status)
        {
            case UserStatus.Active:
                user.Activate(Now.AddDays(-29));
                break;
            case UserStatus.Suspended:
                user.Suspend(Now.AddDays(-29));
                break;
            case UserStatus.Deactivated:
                user.Deactivate(Now.AddDays(-29));
                break;
            case UserStatus.Pending:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled user status.");
        }

        _users.Seed(user);
        _seededUser = user;

        return user;
    }

    /// <summary>
    /// Stores a token for <see cref="PresentedToken"/>, belonging to the seeded user when there is one.
    /// </summary>
    private RefreshToken SeedPresentedToken(DateTimeOffset? expiresAt = null)
    {
        var userId = _seededUser?.Id ?? Guid.NewGuid();

        var refreshToken = new RefreshToken(
            Guid.NewGuid(),
            userId,
            FakeTokenProvider.HashOf(PresentedToken),
            Now.AddDays(-1),
            expiresAt ?? Now.AddDays(6));

        _refreshTokens.Seed(refreshToken);

        return refreshToken;
    }

    private void AssertNothingWasIssuedOrSaved()
    {
        Assert.Equal(0, _tokenProvider.CreateAccessTokenCallCount);
        Assert.Equal(0, _tokenProvider.CreateRefreshTokenCallCount);
        Assert.Empty(_refreshTokens.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }
}
