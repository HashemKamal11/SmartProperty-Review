using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Application.Authentication.PasswordReset;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authentication;

/// <summary>
/// Exercises the three real password-reset handlers (forgot, verify the code, set the new password): the responses must
/// not reveal which emails are registered, a reset code works once, the reset token works once, and a reset signs the
/// account out everywhere.
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
        Assert.Equal("password_reset.invalid_code", (await Verify(firstCode)).Error!.Code);
        Assert.True((await Verify(_email.LatestCode())).IsSuccess);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task Forgot_ForAMalformedEmail_IsAValidationError(string email)
    {
        var result = await Forgot(email);

        Assert.Equal("validation.failed", result.Error!.Code);
    }

    // ---- step two: verify the code

    [Fact]
    public async Task Verify_WithTheRightCode_SpendsItAndReturnsAResetToken()
    {
        var code = SeedCode(RightCode);

        var result = await Verify(RightCode);

        Assert.True(result.IsSuccess);
        Assert.Equal(43, result.Value.ResetToken.Length);
        Assert.Equal(Now.AddMinutes(10), result.Value.ExpiresAt);
        Assert.Equal(Now, code.ConsumedAt);
        Assert.Equal(OneTimeCodePurpose.PasswordResetGrant, Assert.Single(_codes.All, c => c.Id != code.Id).Purpose);
        Assert.Equal("old-hash", _credential.PasswordHash);
        Assert.Equal(0, _hasher.HashCallCount);
    }

    [Fact]
    public async Task Verify_TheSameCodeCannotBeUsedTwice()
    {
        SeedCode(RightCode);
        await Verify(RightCode);

        var again = await Verify(RightCode);

        Assert.Equal("password_reset.invalid_code", again.Error!.Code);
    }

    [Fact]
    public async Task Verify_TheTokenIsStoredOnlyAsAHash()
    {
        SeedCode(RightCode);

        var result = await Verify(RightCode);

        var grant = Assert.Single(_codes.All, c => c.Purpose == OneTimeCodePurpose.PasswordResetGrant);
        Assert.DoesNotContain(result.Value.ResetToken, grant.CodeHash);
        Assert.DoesNotContain(result.Value.ResetToken, result.Value.ToString());
    }

    [Fact]
    public async Task Verify_WithAWrongCode_CountsAnAttemptAndIssuesNothing()
    {
        var code = SeedCode(RightCode);

        var result = await Verify("000000");

        Assert.Equal("password_reset.invalid_code", result.Error!.Code);
        Assert.Equal(1, code.FailedAttempts);
        Assert.Null(code.ConsumedAt);
        Assert.Single(_codes.All);
    }

    [Fact]
    public async Task Verify_AfterFiveWrongCodes_LocksTheCode()
    {
        SeedCode(RightCode);

        for (var attempt = 0; attempt < OneTimeCode.MaxFailedAttempts; attempt++)
        {
            await Verify("000000");
        }

        var result = await Verify(RightCode);

        Assert.Equal("password_reset.too_many_attempts", result.Error!.Code);
        Assert.Single(_codes.All);
    }

    [Fact]
    public async Task Verify_WithAnExpiredCode_IsRefused()
    {
        SeedCode(RightCode);
        _clock.UtcNow = Now.AddMinutes(5);

        var result = await Verify(RightCode);

        Assert.Equal("password_reset.invalid_code", result.Error!.Code);
        Assert.Single(_codes.All);
    }

    [Fact]
    public async Task Verify_WithAnEmailVerificationCode_IsRefused()
    {
        _codes.Seed(OneTimeCodePolicy.Issue(_user.Id, OneTimeCodePurpose.EmailVerification, RightCode, Now));

        var result = await Verify(RightCode);

        Assert.Equal("password_reset.invalid_code", result.Error!.Code);
    }

    [Theory]
    [InlineData("nobody@example.test")]
    public async Task Verify_ForAnUnknownEmail_GivesTheSameErrorAsAWrongCode(string email)
    {
        var result = await CreateVerifyHandler().Handle(new VerifyResetCodeCommand(email, RightCode));

        Assert.Equal("password_reset.invalid_code", result.Error!.Code);
    }

    [Theory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task Verify_ForADisabledAccount_GivesTheSameError(UserStatus status)
    {
        SeedCode(RightCode);

        if (status == UserStatus.Suspended)
        {
            _user.Suspend(Now);
        }
        else
        {
            _user.Deactivate(Now);
        }

        var result = await Verify(RightCode);

        Assert.Equal("password_reset.invalid_code", result.Error!.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task Verify_ForAMalformedEmail_IsAValidationError(string email)
    {
        var result = await CreateVerifyHandler().Handle(new VerifyResetCodeCommand(email, RightCode));

        Assert.Equal("validation.failed", result.Error!.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("12345a")]
    public async Task Verify_WithAMalformedCode_IsRefusedWithoutSpendingAnAttempt(string code)
    {
        var stored = SeedCode(RightCode);

        var result = await Verify(code);

        Assert.Equal("password_reset.invalid_code", result.Error!.Code);
        Assert.Equal(0, stored.FailedAttempts);
    }

    [Fact]
    public async Task Verify_AgainGivesANewTokenAndTheEarlierOneStopsWorking()
    {
        SeedCode(RightCode);
        var first = (await Verify(RightCode)).Value.ResetToken;
        _clock.UtcNow = Now.AddSeconds(61);
        await Forgot(Email);
        var secondCode = _email.LatestCode();
        var second = (await Verify(secondCode)).Value.ResetToken;

        Assert.NotEqual(first, second);
        Assert.Equal("password_reset.invalid_token", (await Reset(first)).Error!.Code);
        Assert.True((await Reset(second)).IsSuccess);
    }

    // ---- step three: the new password

    [Fact]
    public async Task Reset_WithTheToken_ChangesThePasswordAndSpendsTheToken()
    {
        var token = await VerifiedTokenAsync();

        var result = await Reset(token);

        Assert.True(result.IsSuccess);
        Assert.Equal(FakePasswordHasher.RehashedPasswordHash, _credential.PasswordHash);
        Assert.Equal(1, _hasher.HashCallCount);
        Assert.NotNull(Assert.Single(_codes.All, c => c.Purpose == OneTimeCodePurpose.PasswordResetGrant).ConsumedAt);
        Assert.Equal("password_reset.invalid_token", (await Reset(token)).Error!.Code);
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
        var token = await VerifiedTokenAsync();

        var result = await Reset(token);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, active.RevokedAt);
        Assert.Null(other.RevokedAt);
        Assert.Null(expired.RevokedAt);
    }

    [Fact]
    public async Task Reset_WithoutVerifyingFirst_IsRefused()
    {
        var result = await Reset("A".PadRight(43, 'A'));

        Assert.Equal("password_reset.invalid_token", result.Error!.Code);
        Assert.Equal("old-hash", _credential.PasswordHash);
    }

    [Fact]
    public async Task Reset_WithAWrongToken_ChangesNothingAndTheRightOneStillWorks()
    {
        var token = await VerifiedTokenAsync();

        var wrong = await Reset("B".PadRight(43, 'B'));
        var malformed = await Reset("short");
        var right = await Reset(token);

        Assert.Equal("password_reset.invalid_token", wrong.Error!.Code);
        Assert.Equal("password_reset.invalid_token", malformed.Error!.Code);
        Assert.True(right.IsSuccess);
    }

    [Fact]
    public async Task Reset_WithAnExpiredToken_IsRefused()
    {
        var token = await VerifiedTokenAsync();
        _clock.UtcNow = Now.AddMinutes(10);

        var result = await Reset(token);

        Assert.Equal("password_reset.invalid_token", result.Error!.Code);
        Assert.Equal("old-hash", _credential.PasswordHash);
    }

    [Fact]
    public async Task Reset_TheTokenOfAnotherAccountIsRefused()
    {
        var other = new User(Guid.NewGuid(), "other@example.test", "Other", "User", Now);
        other.Activate(Now);
        _users.Seed(other);
        var otherCredential = new UserCredential(other.Id, "other-hash", Now);
        _credentials.Seed(otherCredential);
        var otherToken = OneTimeCodePolicy.GenerateGrantToken();
        _codes.Seed(OneTimeCodePolicy.IssueGrant(other.Id, otherToken, Now));

        var result = await Reset(otherToken);

        Assert.Equal("password_reset.invalid_token", result.Error!.Code);
        Assert.Equal("old-hash", _credential.PasswordHash);
        Assert.Equal("other-hash", otherCredential.PasswordHash);
    }

    [Fact]
    public async Task Reset_ADifferentConfirmation_IsAValidationErrorAndSpendsNothing()
    {
        var token = await VerifiedTokenAsync();
        var saves = _unitOfWork.SaveChangesCallCount;

        var result = await CreateResetHandler().Handle(
            new ResetPasswordCommand(Email, token, NewPassword, NewPassword + "x"));

        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal(saves, _unitOfWork.SaveChangesCallCount);
        Assert.Null(Assert.Single(_codes.All, c => c.Purpose == OneTimeCodePurpose.PasswordResetGrant).ConsumedAt);
        Assert.True((await Reset(token)).IsSuccess);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    public async Task Reset_WithoutAUsablePassword_IsAValidationError(string password)
    {
        var token = await VerifiedTokenAsync();
        var saves = _unitOfWork.SaveChangesCallCount;

        var result = await CreateResetHandler().Handle(new ResetPasswordCommand(Email, token, password, password));

        Assert.Equal("validation.failed", result.Error!.Code);
        Assert.Equal(saves, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Reset_ForAnUnknownEmail_GivesTheSameErrorAsABadToken_AndStillHashes()
    {
        var result = await CreateResetHandler().Handle(
            new ResetPasswordCommand("nobody@example.test", "C".PadRight(43, 'C'), NewPassword, NewPassword));

        Assert.Equal("password_reset.invalid_token", result.Error!.Code);
        Assert.Equal(1, _hasher.DummyVerificationCallCount);
    }

    [Fact]
    public async Task Reset_TheOldCodeNoLongerSetsAPassword()
    {
        // The reset code is only ever accepted by the verify step: presented as a token it is not even well formed.
        SeedCode(RightCode);

        var result = await Reset(RightCode);

        Assert.Equal("password_reset.invalid_token", result.Error!.Code);
        Assert.Equal("old-hash", _credential.PasswordHash);
    }

    // ---- helpers

    private OneTimeCode SeedCode(string code)
    {
        var oneTimeCode = OneTimeCodePolicy.Issue(_user.Id, OneTimeCodePurpose.PasswordReset, code, Now);
        _codes.Seed(oneTimeCode);

        return oneTimeCode;
    }

    private async Task<string> VerifiedTokenAsync()
    {
        SeedCode(RightCode);

        return (await Verify(RightCode)).Value.ResetToken;
    }

    private Task<Result> Forgot(string email)
    {
        return new ForgotPasswordCommandHandler(_users, _codes, _email, _clock, _unitOfWork)
            .Handle(new ForgotPasswordCommand(email));
    }

    private Task<Result<VerifyResetCodeResult>> Verify(string code)
    {
        return CreateVerifyHandler().Handle(new VerifyResetCodeCommand(Email, code));
    }

    private Task<Result> Reset(string token)
    {
        return CreateResetHandler().Handle(new ResetPasswordCommand(Email, token, NewPassword, NewPassword));
    }

    private VerifyResetCodeCommandHandler CreateVerifyHandler()
    {
        return new VerifyResetCodeCommandHandler(_users, _codes, _clock, _unitOfWork);
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
