using SmartProperty.Application.Authentication.EmailVerifications;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authentication;

/// <summary>
/// Exercises the real email verify and resend handlers. Resend works like forgot-password: it answers the same for
/// every email. A code issued for one purpose must never satisfy another.
/// </summary>
public sealed class EmailVerificationHandlerTests
{
    private const string Email = "user@example.test";
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
        _user = new User(Guid.NewGuid(), Email, "Test", "User", Now, "+201012345678");
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
    public async Task UnknownEmail_GivesTheSameErrorAsAWrongCode()
    {
        var result = await new VerifyEmailCommandHandler(_users, _codes, _clock, _unitOfWork)
            .Handle(new VerifyEmailCommand("nobody@example.test", RightCode));

        Assert.Equal("email_verification.invalid_code", result.Error!.Code);
    }

    [Fact]
    public async Task AlreadyVerifiedEmail_GivesTheSameErrorAsAWrongCode()
    {
        SeedCode(OneTimeCodePurpose.EmailVerification, RightCode);
        _user.VerifyEmail(Now);

        var result = await Verify(RightCode);

        Assert.Equal("email_verification.invalid_code", result.Error!.Code);
    }

    [Fact]
    public async Task Resend_AfterTheOldCodeExpired_EmailsANewCode_AndRetiresTheOldOne()
    {
        var old = SeedCode(OneTimeCodePurpose.EmailVerification, RightCode);
        _clock.UtcNow = Now.AddMinutes(6);

        var result = await Resend(Email);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now.AddMinutes(6), old.ConsumedAt);
        var sent = Assert.Single(_email.Sent);
        Assert.Equal(Email, sent.ToEmail);
        Assert.True((await Verify(_email.LatestCode())).IsSuccess);
    }

    [Fact]
    public async Task Resend_WithinTheCooldown_AnswersTheSameAndSendsNothing()
    {
        SeedCode(OneTimeCodePurpose.EmailVerification, RightCode);
        _clock.UtcNow = Now.AddSeconds(59);

        var result = await Resend(Email);

        Assert.True(result.IsSuccess);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Resend_ForAVerifiedEmail_AnswersTheSameAndSendsNothing()
    {
        _user.VerifyEmail(Now);

        var result = await Resend(Email);

        Assert.True(result.IsSuccess);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Resend_ForAnUnknownEmail_AnswersTheSameAndSendsNothing()
    {
        var result = await Resend("nobody@example.test");

        Assert.True(result.IsSuccess);
        Assert.Empty(_email.Sent);
        Assert.Empty(_codes.All);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task Resend_ForAMalformedEmail_IsAValidationError(string email)
    {
        var result = await Resend(email);

        Assert.Equal("validation.failed", result.Error!.Code);
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
            .Handle(new VerifyEmailCommand(Email, code));
    }

    private Task<Result> Resend(string email)
    {
        return new ResendEmailVerificationCommandHandler(_users, _codes, _email, _clock, _unitOfWork)
            .Handle(new ResendEmailVerificationCommand(email));
    }
}
