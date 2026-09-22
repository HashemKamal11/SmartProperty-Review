using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Authentication.Login;
using SmartProperty.Domain.Identity;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authentication;

/// <summary>
/// Exercises the real <see cref="LoginCommandHandler"/> against test doubles.
/// </summary>
/// <remarks>
/// The timing hardening is asserted as control flow — which verification ran, and how many times — never as
/// elapsed time. A stopwatch assertion would be non-deterministic on a shared machine and would not actually
/// prove that the dummy verification happened.
/// </remarks>
public sealed class LoginCommandHandlerTests
{
    private const string Email = "owner@example.com";
    private const string Password = "correct-horse-battery-staple";
    private const string StoredPasswordHash = "stored-password-hash";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();
    private readonly FakeUserCredentialRepository _credentials = new();
    private readonly FakeRefreshTokenRepository _refreshTokens = new();
    private readonly FakePasswordHasher _passwordHasher = new();
    private readonly FakeTokenProvider _tokenProvider = new(Now.AddDays(7));
    private readonly FakeDateTimeProvider _dateTimeProvider = new(Now);
    private readonly FakeUnitOfWork _unitOfWork = new();

    [Fact]
    public async Task UnknownUser_ReturnsGenericInvalidCredentials()
    {
        var result = await CreateHandler().Handle(new LoginCommand(Email, Password));

        Assert.True(result.IsFailure);
        Assert.Same(LoginErrors.InvalidCredentials, result.Error);
    }

    [Fact]
    public async Task UnknownUser_PerformsExactlyOneDummyVerificationAndNoRealVerification()
    {
        await CreateHandler().Handle(new LoginCommand(Email, Password));

        Assert.Equal(1, _passwordHasher.DummyVerificationCallCount);
        Assert.Equal(0, _passwordHasher.VerifyCallCount);
        Assert.Equal(Password, _passwordHasher.LastDummyVerificationPassword);
    }

    [Fact]
    public async Task UnknownUser_IssuesNoTokensAndSavesNothing()
    {
        await CreateHandler().Handle(new LoginCommand(Email, Password));

        AssertNothingWasIssuedOrSaved();
    }

    [Fact]
    public async Task MissingCredential_ReturnsGenericInvalidCredentials()
    {
        SeedUser(UserStatus.Active);

        var result = await CreateHandler().Handle(new LoginCommand(Email, Password));

        Assert.True(result.IsFailure);
        Assert.Same(LoginErrors.InvalidCredentials, result.Error);
    }

    [Fact]
    public async Task MissingCredential_PerformsExactlyOneDummyVerificationAndNoRealVerification()
    {
        SeedUser(UserStatus.Active);

        await CreateHandler().Handle(new LoginCommand(Email, Password));

        Assert.Equal(1, _passwordHasher.DummyVerificationCallCount);
        Assert.Equal(0, _passwordHasher.VerifyCallCount);
    }

    [Fact]
    public async Task MissingCredential_IssuesNoTokensAndSavesNothing()
    {
        SeedUser(UserStatus.Active);

        await CreateHandler().Handle(new LoginCommand(Email, Password));

        AssertNothingWasIssuedOrSaved();
    }

    [Fact]
    public async Task WrongPassword_ReturnsGenericInvalidCredentials()
    {
        SeedUserWithCredential(UserStatus.Active);
        _passwordHasher.VerifyResult = PasswordVerificationStatus.Failed;

        var result = await CreateHandler().Handle(new LoginCommand(Email, Password));

        Assert.True(result.IsFailure);
        Assert.Same(LoginErrors.InvalidCredentials, result.Error);
    }

    [Fact]
    public async Task WrongPassword_PerformsExactlyOneRealVerificationAndNoDummyVerification()
    {
        SeedUserWithCredential(UserStatus.Active);
        _passwordHasher.VerifyResult = PasswordVerificationStatus.Failed;

        await CreateHandler().Handle(new LoginCommand(Email, Password));

        Assert.Equal(1, _passwordHasher.VerifyCallCount);
        Assert.Equal(0, _passwordHasher.DummyVerificationCallCount);
        Assert.Equal(Password, _passwordHasher.LastVerifiedPassword);
        Assert.Equal(StoredPasswordHash, _passwordHasher.LastVerifiedPasswordHash);
    }

    [Fact]
    public async Task WrongPassword_IssuesNoTokensAndSavesNothing()
    {
        SeedUserWithCredential(UserStatus.Active);
        _passwordHasher.VerifyResult = PasswordVerificationStatus.Failed;

        await CreateHandler().Handle(new LoginCommand(Email, Password));

        AssertNothingWasIssuedOrSaved();
    }

    [Fact]
    public async Task UnrecognizedVerificationStatus_FailsClosed()
    {
        SeedUserWithCredential(UserStatus.Active);
        _passwordHasher.VerifyResult = (PasswordVerificationStatus)99;

        var result = await CreateHandler().Handle(new LoginCommand(Email, Password));

        Assert.True(result.IsFailure);
        Assert.Same(LoginErrors.InvalidCredentials, result.Error);
        AssertNothingWasIssuedOrSaved();
    }

    [Fact]
    public async Task ActiveUserWithCorrectPassword_Succeeds()
    {
        SeedUserWithCredential(UserStatus.Active);

        var result = await CreateHandler().Handle(new LoginCommand(Email, Password));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _passwordHasher.VerifyCallCount);
        Assert.Equal(0, _passwordHasher.DummyVerificationCallCount);
    }

    [Fact]
    public async Task ActiveUserWithCorrectPassword_ReturnsTheGeneratedTokens()
    {
        var (user, _) = SeedUserWithCredential(UserStatus.Active);

        var result = await CreateHandler().Handle(new LoginCommand(Email, Password));

        var generated = Assert.Single(_tokenProvider.GeneratedRefreshTokens);
        Assert.Equal(1, _tokenProvider.CreateAccessTokenCallCount);
        Assert.Equal($"{FakeTokenProvider.AccessTokenPrefix}{user.Id}", result.Value.AccessToken);
        Assert.Equal(generated.Value, result.Value.RefreshToken);
        Assert.Equal(generated.ExpiresAt, result.Value.RefreshTokenExpiresAt);
    }

    [Fact]
    public async Task ActiveUserWithCorrectPassword_PersistsOnlyTheRefreshTokenHash()
    {
        var (user, _) = SeedUserWithCredential(UserStatus.Active);

        var result = await CreateHandler().Handle(new LoginCommand(Email, Password));

        var stored = Assert.Single(_refreshTokens.Added);
        var generated = Assert.Single(_tokenProvider.GeneratedRefreshTokens);

        Assert.Equal(generated.TokenHash, stored.TokenHash);
        Assert.NotEqual(generated.Value, stored.TokenHash);
        Assert.DoesNotContain(result.Value.RefreshToken, stored.TokenHash, StringComparison.Ordinal);
        Assert.Equal(user.Id, stored.UserId);
        Assert.Equal(Now, stored.CreatedAt);
        Assert.Equal(generated.ExpiresAt, stored.ExpiresAt);
        Assert.Null(stored.RevokedAt);
    }

    [Fact]
    public async Task ActiveUserWithCorrectPassword_CommitsExactlyOnce()
    {
        SeedUserWithCredential(UserStatus.Active);

        await CreateHandler().Handle(new LoginCommand(Email, Password));

        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task NonActiveUserWithCorrectPassword_ReturnsAccountUnavailable(UserStatus status)
    {
        SeedUserWithCredential(status);

        var result = await CreateHandler().Handle(new LoginCommand(Email, Password));

        Assert.True(result.IsFailure);
        Assert.Same(LoginErrors.AccountUnavailable, result.Error);
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task NonActiveUserWithCorrectPassword_VerifiesThePasswordFirst(UserStatus status)
    {
        // The status must never be revealed to a caller who does not know the password, so verification has to
        // have happened before the status was consulted.
        SeedUserWithCredential(status);

        await CreateHandler().Handle(new LoginCommand(Email, Password));

        Assert.Equal(1, _passwordHasher.VerifyCallCount);
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task NonActiveUserWithCorrectPassword_IssuesNoTokensAndSavesNothing(UserStatus status)
    {
        SeedUserWithCredential(status);

        await CreateHandler().Handle(new LoginCommand(Email, Password));

        AssertNothingWasIssuedOrSaved();
    }

    [Fact]
    public async Task NonActiveUserNeedingARehash_DoesNotRewriteTheStoredHash()
    {
        // Nothing is changed for a rejected login, not even a needed re-hash.
        var credential = SeedUserWithCredential(UserStatus.Suspended).Credential;
        _passwordHasher.VerifyResult = PasswordVerificationStatus.SuccessRehashNeeded;

        await CreateHandler().Handle(new LoginCommand(Email, Password));

        Assert.Equal(StoredPasswordHash, credential.PasswordHash);
        Assert.Equal(0, _passwordHasher.HashCallCount);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task RehashNeeded_SucceedsAndReplacesTheStoredHash()
    {
        var credential = SeedUserWithCredential(UserStatus.Active).Credential;
        _passwordHasher.VerifyResult = PasswordVerificationStatus.SuccessRehashNeeded;

        var result = await CreateHandler().Handle(new LoginCommand(Email, Password));

        Assert.True(result.IsSuccess);
        Assert.Equal(FakePasswordHasher.RehashedPasswordHash, credential.PasswordHash);
        Assert.Equal(Now, credential.UpdatedAt);
        Assert.Equal(1, _passwordHasher.HashCallCount);
    }

    [Fact]
    public async Task RehashNeeded_CommitsTheNewHashAndTheRefreshTokenInOneSave()
    {
        SeedUserWithCredential(UserStatus.Active);
        _passwordHasher.VerifyResult = PasswordVerificationStatus.SuccessRehashNeeded;

        await CreateHandler().Handle(new LoginCommand(Email, Password));

        Assert.Single(_refreshTokens.Added);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task InvalidCommand_IsRejectedBeforeAnyRepositoryOrHasherIsTouched()
    {
        var result = await CreateHandler().Handle(new LoginCommand("not-an-email", Password));

        Assert.True(result.IsFailure);
        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal(0, _users.GetByEmailCallCount);
        Assert.Equal(0, _passwordHasher.DummyVerificationCallCount);
        Assert.Equal(0, _passwordHasher.VerifyCallCount);
    }

    [Fact]
    public async Task NullCommand_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateHandler().Handle(null!));
    }

    private LoginCommandHandler CreateHandler()
    {
        return new LoginCommandHandler(
            _users,
            _credentials,
            _refreshTokens,
            _passwordHasher,
            _tokenProvider,
            _dateTimeProvider,
            _unitOfWork);
    }

    private User SeedUser(UserStatus status)
    {
        var user = new User(Guid.NewGuid(), Email, "Owner", "Example", Now.AddDays(-30));

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

        return user;
    }

    private (User User, UserCredential Credential) SeedUserWithCredential(UserStatus status)
    {
        var user = SeedUser(status);
        var credential = new UserCredential(user.Id, StoredPasswordHash, Now.AddDays(-30));

        _credentials.Seed(credential);

        return (user, credential);
    }

    private void AssertNothingWasIssuedOrSaved()
    {
        Assert.Equal(0, _tokenProvider.CreateAccessTokenCallCount);
        Assert.Equal(0, _tokenProvider.CreateRefreshTokenCallCount);
        Assert.Empty(_refreshTokens.Added);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }
}
