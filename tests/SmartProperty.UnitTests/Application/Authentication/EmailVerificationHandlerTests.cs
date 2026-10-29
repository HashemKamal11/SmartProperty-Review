using SmartProperty.Application.Authentication.EmailVerifications;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authentication;

/// <summary>
/// Exercises the real email verify and resend handlers. They follow the same rules as phone verification, and a
/// code issued for one purpose must never satisfy another.
/// </summary>
public sealed class EmailVerificationHandlerTests
{
    private const string RightCode = "123456";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();
    private readonly FakeOneTimeCodeRepository _codes = new();
    private readonly FakeEmailSender _email = new();
    private readonly FakeDateTimeProvider _clock = new(Now);
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly User _user;

    public EmailVerificationHandlerTests()
    {
        _user = new User(Guid.NewGuid(), "user@example.test", "Test", "User", Now, "+201012345678");
        _users.Seed(_user);
    }

    [Fact]
    public async Task RightCode_VerifiesTheEmail()
    {
        var code = SeedCode(OneTimeCodePurpose.EmailVerification, RightCode);

        var result = await Verify(RightCode);

        Assert.True(result.IsSuccess);
        Assert.True(_user.IsEmailVerified);
        Assert.Equal(Now, _user.EmailVerifiedAt);
        Assert.Equal(Now, code.ConsumedAt);
        Assert.False(_user.IsPhoneVerified);
    }

    [Fact]
    public async Task WrongCode_IsRefusedAndCountsAnAttempt()
    {
        var code = SeedCode(OneTimeCodePurpose.EmailVerification, RightCode);

        var result = await Verify("000000");

        Assert.Equal("email_verification.invalid_code", result.Error!.Code);
        Assert.False(_user.IsEmailVerified);
        Assert.Equal(1, code.FailedAttempts);
    }

    [Fact]
    public async Task APhoneCode_DoesNotVerifyTheEmail()
    {
        SeedCode(OneTimeCodePurpose.PhoneVerification, RightCode);

        var result = await Verify(RightCode);

        Assert.Equal("email_verification.invalid_code", result.Error!.Code);
        Assert.False(_user.IsEmailVerified);
    }

    [Fact]
    public async Task ExpiredCode_IsRefused()
    {
        SeedCode(OneTimeCodePurpose.EmailVerification, RightCode);
        _clock.UtcNow = Now.AddMinutes(5);

        var result = await Verify(RightCode);

        Assert.Equal("email_verification.invalid_code", result.Error!.Code);
    }

    [Fact]
    public async Task Resend_EmailsANewCode_AndRetiresTheOldOne()
    {
        var old = SeedCode(OneTimeCodePurpose.EmailVerification, RightCode);
        _clock.UtcNow = Now.AddSeconds(60);

        var result = await CreateResendHandler().Handle(new ResendEmailVerificationCommand(_user.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(Now.AddSeconds(60), old.ConsumedAt);
        var sent = Assert.Single(_email.Sent);
        Assert.Equal("user@example.test", sent.ToEmail);
        Assert.True((await Verify(_email.LatestCode())).IsSuccess);
    }

    [Fact]
    public async Task Resend_WithinTheCooldown_IsRefusedAndSendsNothing()
    {
        SeedCode(OneTimeCodePurpose.EmailVerification, RightCode);
        _clock.UtcNow = Now.AddSeconds(59);

        var result = await CreateResendHandler().Handle(new ResendEmailVerificationCommand(_user.Id));

        Assert.Equal("email_verification.resend_too_soon", result.Error!.Code);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Resend_ForAVerifiedEmail_IsRefused()
    {
        _user.VerifyEmail(Now);

        var result = await CreateResendHandler().Handle(new ResendEmailVerificationCommand(_user.Id));

        Assert.Equal("email_verification.already_verified", result.Error!.Code);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public void ContactVerification_IsCompleteOnlyWhenPhoneAndEmailAreBothVerified()
    {
        Assert.False(_user.IsContactVerificationComplete);

        _user.VerifyPhone(Now);
        Assert.False(_user.IsContactVerificationComplete);

        _user.VerifyEmail(Now);
        Assert.True(_user.IsContactVerificationComplete);
    }

    [Fact]
    public void AccountWithoutAPhoneNumber_IsNotHeldBack()
    {
        var legacy = new User(Guid.NewGuid(), "old@example.test", "Old", "User", Now);

        Assert.True(legacy.IsContactVerificationComplete);
    }

    private OneTimeCode SeedCode(OneTimeCodePurpose purpose, string code)
    {
        var oneTimeCode = OneTimeCodePolicy.Issue(_user.Id, purpose, code, Now);
        _codes.Seed(oneTimeCode);

        return oneTimeCode;
    }

    private Task<Result<EmailVerified>> Verify(string code)
    {
        return new VerifyEmailCommandHandler(_users, _codes, _clock, _unitOfWork)
            .Handle(new VerifyEmailCommand(_user.Id, code));
    }

    private ResendEmailVerificationCommandHandler CreateResendHandler()
    {
        return new ResendEmailVerificationCommandHandler(_users, _codes, _email, _clock, _unitOfWork);
    }
}
