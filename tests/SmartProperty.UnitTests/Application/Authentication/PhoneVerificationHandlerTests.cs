using SmartProperty.Application.Authentication.PhoneVerifications;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authentication;

/// <summary>
/// Exercises the real verify and resend handlers against the approved rules: six digits, five minutes, five wrong
/// attempts, and a one-minute resend cooldown.
/// </summary>
public sealed class PhoneVerificationHandlerTests
{
    private const string Phone = "+201012345678";
    private const string RightCode = "123456";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();
    private readonly FakePhoneVerificationRepository _verifications = new();
    private readonly FakeSmsSender _sms = new();
    private readonly FakeDateTimeProvider _clock = new(Now);
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly User _user;

    public PhoneVerificationHandlerTests()
    {
        _user = new User(Guid.NewGuid(), "user@example.test", "Test", "User", Now, Phone);
        _users.Seed(_user);
    }

    [Fact]
    public async Task RightCode_VerifiesThePhoneAndConsumesTheCode()
    {
        var verification = SeedCode(RightCode);

        var result = await Verify(RightCode);

        Assert.True(result.IsSuccess);
        Assert.True(_user.IsPhoneVerified);
        Assert.Equal(Now, _user.PhoneVerifiedAt);
        Assert.Equal(Now, verification.ConsumedAt);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task WrongCode_IsRefusedAndCountsOneAttempt()
    {
        var verification = SeedCode(RightCode);

        var result = await Verify("000000");

        Assert.Equal("phone_verification.invalid_code", result.Error!.Code);
        Assert.False(_user.IsPhoneVerified);
        Assert.Equal(1, verification.FailedAttempts);
        Assert.Equal(1, _unitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task FifthWrongCode_LocksTheCode_SoEvenTheRightOneIsRefused()
    {
        SeedCode(RightCode);

        for (var attempt = 1; attempt < PhoneVerification.MaxFailedAttempts; attempt++)
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
    public async Task UsedCode_CannotBeUsedTwice()
    {
        SeedCode(RightCode);
        Assert.True((await Verify(RightCode)).IsSuccess);

        var again = await Verify(RightCode);

        Assert.Equal("phone_verification.already_verified", again.Error!.Code);
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
    public async Task UnknownUser_IsNotFound()
    {
        var result = await CreateVerifyHandler().Handle(new VerifyPhoneCommand(Guid.NewGuid(), RightCode));

        Assert.Equal("phone_verification.user_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task Resend_SendsANewCode_AndRetiresTheOldOne()
    {
        var old = SeedCode(RightCode);
        _clock.UtcNow = Now.AddSeconds(60);

        var result = await CreateResendHandler().Handle(new ResendPhoneVerificationCommand(_user.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(Now.AddSeconds(60).AddMinutes(5), result.Value.ExpiresAt);
        Assert.Equal(Now.AddSeconds(60), old.ConsumedAt);
        var sent = Assert.Single(_sms.Sent);
        Assert.Equal(Phone, sent.PhoneNumber);
        Assert.Equal(2, _verifications.All.Count);

        // Only the newest code works.
        Assert.Equal("phone_verification.invalid_code", (await Verify(RightCode)).Error!.Code);
        Assert.True((await Verify(_sms.LatestCode())).IsSuccess);
    }

    [Fact]
    public async Task Resend_WithinTheCooldown_IsRefusedAndSendsNothing()
    {
        SeedCode(RightCode);
        _clock.UtcNow = Now.AddSeconds(59);

        var result = await CreateResendHandler().Handle(new ResendPhoneVerificationCommand(_user.Id));

        Assert.Equal("phone_verification.resend_too_soon", result.Error!.Code);
        Assert.Empty(_sms.Sent);
        Assert.Single(_verifications.All);
    }

    [Fact]
    public async Task Resend_ForAVerifiedPhone_IsRefused()
    {
        _user.VerifyPhone(Now);

        var result = await CreateResendHandler().Handle(new ResendPhoneVerificationCommand(_user.Id));

        Assert.Equal("phone_verification.already_verified", result.Error!.Code);
        Assert.Empty(_sms.Sent);
    }

    private PhoneVerification SeedCode(string code)
    {
        var verification = PhoneVerificationPolicy.Issue(_user.Id, code, Now);
        _verifications.Seed(verification);

        return verification;
    }

    private Task<Result<PhoneVerified>> Verify(string code)
    {
        return CreateVerifyHandler().Handle(new VerifyPhoneCommand(_user.Id, code));
    }

    private VerifyPhoneCommandHandler CreateVerifyHandler()
    {
        return new VerifyPhoneCommandHandler(_users, _verifications, _clock, _unitOfWork);
    }

    private ResendPhoneVerificationCommandHandler CreateResendHandler()
    {
        return new ResendPhoneVerificationCommandHandler(_users, _verifications, _sms, _clock, _unitOfWork);
    }
}
