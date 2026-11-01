using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Application.Authentication.PasswordReset;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authentication;

/// <summary>
/// Exercises the real forgot and reset password handlers: the response must not reveal which emails are
/// registered, a reset code works once, and a reset signs the account out everywhere.
/// </summary>
public sealed class PasswordResetHandlerTests
{
    private const string Email = "user@example.test";
    private const string RightCode = "123456";
    private const string NewPassword = "a-brand-new-password";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();
    private readonly FakeUserCredentialRepository _credentials = new();
    private readonly FakeRefreshTokenRepository _refreshTokens = new();
    private readonly FakeOneTimeCodeRepository _codes = new();
    private readonly FakePasswordHasher _hasher = new();
    private readonly FakeEmailSender _email = new();
    private readonly FakeDateTimeProvider _clock = new(Now);
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly User _user;
    private readonly UserCredential _credential;

    public PasswordResetHandlerTests()
    {
        _user = new User(Guid.NewGuid(), Email, "Test", "User", Now);
        _user.Activate(Now);
        _users.Seed(_user);

        _credential = new UserCredential(_user.Id, "old-hash", Now);
        _credentials.Seed(_credential);
    }

    [Fact]
    public async Task Forgot_ForARegisteredEmail_SendsACodeByEmail()
    {
        var result = await Forgot(Email);

        Assert.True(result.IsSuccess);
        var sent = Assert.Single(_email.Sent);
        Assert.Equal(Email, sent.ToEmail);
        Assert.Equal(6, _email.LatestCode().Length);
        Assert.Single(_codes.All);
        Assert.Equal(OneTimeCodePurpose.PasswordReset, _codes.All[0].Purpose);
    }

    [Fact]
    public async Task Forgot_ForAnUnknownEmail_AnswersTheSameAndSendsNothing()
    {
        var result = await Forgot("nobody@example.test");

        Assert.True(result.IsSuccess);
        Assert.Empty(_email.Sent);
        Assert.Empty(_codes.All);
    }

    [Theory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task Forgot_ForADisabledAccount_AnswersTheSameAndSendsNothing(UserStatus status)
    {
        if (status == UserStatus.Suspended)
        {
            _user.Suspend(Now);
        }
        else
        {
            _user.Deactivate(Now);
        }

        var result = await Forgot(Email);

        Assert.True(result.IsSuccess);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Forgot_InsideTheCooldown_AnswersTheSameAndSendsNothingMore()
    {
        await Forgot(Email);
        _clock.UtcNow = Now.AddSeconds(59);

        var second = await Forgot(Email);

        Assert.True(second.IsSuccess);
        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task Forgot_AfterTheCooldown_SendsANewCodeAndRetiresTheOld()
    {
        await Forgot(Email);
        var firstCode = _email.LatestCode();
        _clock.UtcNow = Now.AddSeconds(60);

        await Forgot(Email);

        Assert.Equal(2, _email.Sent.Count);
        Assert.Equal("password_reset.invalid_code", (await Reset(firstCode)).Error!.Code);
        Assert.True((await Reset(_email.LatestCode())).IsSuccess);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task Forgot_ForAMalformedEmail_IsAValidationError(string email)
    {
        var result = await Forgot(email);

        Assert.Equal("validation.failed", result.Error!.Code);
    }

    [Fact]
    public async Task Reset_WithTheRightCode_ChangesThePasswordAndSpendsTheCode()
    {
        var code = SeedCode(RightCode);

        var result = await Reset(RightCode);

        Assert.True(result.IsSuccess);
        Assert.Equal(FakePasswordHasher.RehashedPasswordHash, _credential.PasswordHash);
        Assert.Equal(Now, code.ConsumedAt);
        Assert.Equal(1, _hasher.HashCallCount);
        Assert.Equal("password_reset.invalid_code", (await Reset(RightCode)).Error!.Code);
    }

    [Fact]
    public async Task Reset_RevokesEveryActiveRefreshToken_ButLeavesOthersAlone()
    {
        var active = new RefreshToken(Guid.NewGuid(), _user.Id, "active", Now.AddDays(-1), Now.AddDays(6));
        var other = new RefreshToken(Guid.NewGuid(), Guid.NewGuid(), "other-user", Now.AddDays(-1), Now.AddDays(6));
        var expired = new RefreshToken(Guid.NewGuid(), _user.Id, "expired", Now.AddDays(-8), Now.AddDays(-1));
        _refreshTokens.Seed(active);
        _refreshTokens.Seed(other);
        _refreshTokens.Seed(expired);
        SeedCode(RightCode);

        var result = await Reset(RightCode);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, active.RevokedAt);
        Assert.Null(other.RevokedAt);
        Assert.Null(expired.RevokedAt);
    }

    [Fact]
    public async Task Reset_WithAWrongCode_ChangesNothingAndCountsAnAttempt()
    {
        var code = SeedCode(RightCode);

        var result = await Reset("000000");

        Assert.Equal("password_reset.invalid_code", result.Error!.Code);
        Assert.Equal("old-hash", _credential.PasswordHash);
        Assert.Equal(1, code.FailedAttempts);
        Assert.Equal(0, _hasher.HashCallCount);
    }

    [Fact]
    public async Task Reset_AfterFiveWrongCodes_LocksTheCode()
    {
        SeedCode(RightCode);

        for (var attempt = 0; attempt < OneTimeCode.MaxFailedAttempts; attempt++)
        {
            await Reset("000000");
        }

        var result = await Reset(RightCode);

        Assert.Equal("password_reset.too_many_attempts", result.Error!.Code);
        Assert.Equal("old-hash", _credential.PasswordHash);
    }

    [Fact]
    public async Task Reset_WithAnExpiredCode_IsRefused()
    {
        SeedCode(RightCode);
        _clock.UtcNow = Now.AddMinutes(5);

        var result = await Reset(RightCode);

        Assert.Equal("password_reset.invalid_code", result.Error!.Code);
        Assert.Equal("old-hash", _credential.PasswordHash);
    }

    [Fact]
    public async Task Reset_WithAnEmailVerificationCode_IsRefused()
    {
        _codes.Seed(OneTimeCodePolicy.Issue(_user.Id, OneTimeCodePurpose.EmailVerification, RightCode, Now));

        var result = await Reset(RightCode);

        Assert.Equal("password_reset.invalid_code", result.Error!.Code);
        Assert.Equal("old-hash", _credential.PasswordHash);
    }

    [Fact]
    public async Task Reset_ForAnUnknownEmail_GivesTheSameErrorAsAWrongCode_AndStillHashes()
    {
        var result = await CreateResetHandler().Handle(
            new ResetPasswordCommand("nobody@example.test", RightCode, NewPassword));

        Assert.Equal("password_reset.invalid_code", result.Error!.Code);
        Assert.Equal(1, _hasher.DummyVerificationCallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Reset_WithoutANewPassword_IsAValidationError(string password)
    {
        SeedCode(RightCode);

        var result = await CreateResetHandler().Handle(new ResetPasswordCommand(Email, RightCode, password));

        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    private OneTimeCode SeedCode(string code)
    {
        var oneTimeCode = OneTimeCodePolicy.Issue(_user.Id, OneTimeCodePurpose.PasswordReset, code, Now);
        _codes.Seed(oneTimeCode);

        return oneTimeCode;
    }

    private Task<Result> Forgot(string email)
    {
        return new ForgotPasswordCommandHandler(_users, _codes, _email, _clock, _unitOfWork)
            .Handle(new ForgotPasswordCommand(email));
    }

    private Task<Result> Reset(string code)
    {
        return CreateResetHandler().Handle(new ResetPasswordCommand(Email, code, NewPassword));
    }

    private ResetPasswordCommandHandler CreateResetHandler()
    {
        return new ResetPasswordCommandHandler(
            _users,
            _credentials,
            _refreshTokens,
            _codes,
            _hasher,
            _clock,
            _unitOfWork);
    }
}
