using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Application.Authentication.PhoneVerifications;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authentication;

/// <summary>
/// Exercises the real verify and resend handlers against the approved rules: six digits, five minutes, five wrong
/// attempts, and a one-minute resend cooldown. The account is found by email, so the answers must not depend on
/// whether an email is registered.
/// </summary>
public sealed class PhoneVerificationHandlerTests
{
    private const string Email = "user@example.test";
    private const string Phone = "+201012345678";
    private const string RightCode = "123456";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();
    private readonly FakeOneTimeCodeRepository _codes = new();
    private readonly FakeSmsSender _sms = new();
    private readonly FakeDateTimeProvider _clock = new(Now);
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly User _user;

    public PhoneVerificationHandlerTests()
    {
        _user = new User(Guid.NewGuid(), Email, "Test", "User", Now, Phone);
        _users.Seed(_user);
    }

    [Fact]
    public async Task RightCode_VerifiesThePhoneAndConsumesTheCode()
    {
        var code = SeedCode(RightCode);

        var result = await Verify(RightCode);

        Assert.True(result.IsSuccess);
        Assert.Equal(_user.Id, result.Value.UserId);
        Assert.True(_user.IsPhoneVerified);
        Assert.Equal(Now, _user.PhoneVerifiedAt);
        Assert.Equal(Now, code.ConsumedAt);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task PhoneNumberIsMatchedWithoutRegardToSeparators()
    {
        SeedCode(RightCode);

        var result = await CreateVerifyHandler().Handle(new VerifyPhoneCommand(" +20 (101) 234-5678 ", RightCode));

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task WrongCode_IsRefusedAndCountsOneAttempt()
    {
        var code = SeedCode(RightCode);

        var result = await Verify("000000");

        Assert.Equal("phone_verification.invalid_code", result.Error!.Code);
        Assert.False(_user.IsPhoneVerified);
        Assert.Equal(1, code.FailedAttempts);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task FifthWrongCode_LocksTheCode_SoEvenTheRightOneIsRefused()
    {
        SeedCode(RightCode);

        for (var attempt = 1; attempt < OneTimeCode.MaxFailedAttempts; attempt++)
        {
            Assert.Equal("phone_verification.invalid_code", (await Verify("000000")).Error!.Code);
        }

        var fifth = await Verify("000000");
        var afterLock = await Verify(RightCode);

        Assert.Equal("phone_verification.too_many_attempts", fifth.Error!.Code);
        Assert.Equal("phone_verification.too_many_attempts", afterLock.Error!.Code);
        Assert.False(_user.IsPhoneVerified);
    }

    [Fact]
    public async Task ExpiredCode_IsRefusedEvenWhenItIsRight()
    {
        SeedCode(RightCode);
        _clock.UtcNow = Now.AddMinutes(5);

        var result = await Verify(RightCode);

        Assert.Equal("phone_verification.invalid_code", result.Error!.Code);
        Assert.False(_user.IsPhoneVerified);
    }

    [Fact]
    public async Task CodeJustBeforeExpiry_StillWorks()
    {
        SeedCode(RightCode);
        _clock.UtcNow = Now.AddMinutes(5).AddTicks(-1);

        Assert.True((await Verify(RightCode)).IsSuccess);
    }

    [Fact]
    public async Task UsedCode_CannotBeUsedTwice_AndTheAnswerIsTheSameAsAWrongCode()
    {
        SeedCode(RightCode);
        Assert.True((await Verify(RightCode)).IsSuccess);

        var again = await Verify(RightCode);

        Assert.Equal("phone_verification.invalid_code", again.Error!.Code);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("")]
    public async Task MalformedCode_IsRefusedWithoutCountingAnAttempt(string code)
    {
        var verification = SeedCode(RightCode);

        var result = await Verify(code);

        Assert.Equal("phone_verification.invalid_code", result.Error!.Code);
        Assert.Equal(0, verification.FailedAttempts);
        Assert.Equal(0, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task UnknownPhoneNumber_GivesTheSameErrorAsAWrongCode()
    {
        var result = await CreateVerifyHandler().Handle(new VerifyPhoneCommand("+201099999999", RightCode));

        Assert.Equal("phone_verification.invalid_code", result.Error!.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("01012345678")]
    public async Task MalformedPhoneNumber_IsAValidationError(string phoneNumber)
    {
        var result = await CreateVerifyHandler().Handle(new VerifyPhoneCommand(phoneNumber, RightCode));

        Assert.Equal("validation.failed", result.Error!.Code);
    }

    [Fact]
    public async Task Resend_AfterTheOldCodeExpired_SendsANewCode_AndRetiresTheOldOne()
    {
        var old = SeedCode(RightCode);
        _clock.UtcNow = Now.AddMinutes(6);

        var result = await Resend(Phone);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now.AddMinutes(6), old.ConsumedAt);
        var sent = Assert.Single(_sms.Sent);
        Assert.Equal(Phone, sent.PhoneNumber);
        Assert.Equal(2, _codes.All.Count);

        // Only the newest code works.
        Assert.Equal("phone_verification.invalid_code", (await Verify(RightCode)).Error!.Code);
        Assert.True((await Verify(_sms.LatestCode())).IsSuccess);
    }

    [Fact]
    public async Task Resend_WithinTheCooldown_AnswersTheSameAndSendsNothing()
    {
        SeedCode(RightCode);
        _clock.UtcNow = Now.AddSeconds(59);

        var result = await Resend(Phone);

        Assert.True(result.IsSuccess);
        Assert.Empty(_sms.Sent);
        Assert.Single(_codes.All);
    }

    [Fact]
    public async Task Resend_ForAVerifiedPhone_AnswersTheSameAndSendsNothing()
    {
        _user.VerifyPhone(Now);

        var result = await Resend(Phone);

        Assert.True(result.IsSuccess);
        Assert.Empty(_sms.Sent);
    }

    [Fact]
    public async Task Resend_ForAnUnknownPhoneNumber_AnswersTheSameAndSendsNothing()
    {
        var result = await Resend("+201099999999");

        Assert.True(result.IsSuccess);
        Assert.Empty(_sms.Sent);
        Assert.Empty(_codes.All);
    }

    [Fact]
    public async Task Resend_ForADisabledAccount_AnswersTheSameAndSendsNothing()
    {
        _user.Suspend(Now);

        var result = await Resend(Phone);

        Assert.True(result.IsSuccess);
        Assert.Empty(_sms.Sent);
    }

    [Fact]
    public async Task Resend_ForAMalformedPhoneNumber_IsAValidationError()
    {
        var result = await Resend("01012345678");

        Assert.Equal("validation.failed", result.Error!.Code);
    }

    private OneTimeCode SeedCode(string code)
    {
        var verification = OneTimeCodePolicy.Issue(_user.Id, OneTimeCodePurpose.PhoneVerification, code, Now);
        _codes.Seed(verification);

        return verification;
    }

    private Task<Result<PhoneVerified>> Verify(string code)
    {
        return CreateVerifyHandler().Handle(new VerifyPhoneCommand(Phone, code));
    }

    private Task<Result> Resend(string phoneNumber)
    {
        return new ResendPhoneVerificationCommandHandler(_users, _codes, _sms, _clock, _unitOfWork)
            .Handle(new ResendPhoneVerificationCommand(phoneNumber));
    }

    private VerifyPhoneCommandHandler CreateVerifyHandler()
    {
        return new VerifyPhoneCommandHandler(_users, _codes, _clock, _unitOfWork);
    }
}
