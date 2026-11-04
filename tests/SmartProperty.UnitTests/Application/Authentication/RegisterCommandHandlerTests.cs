using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Authentication.OneTimeCodes;
using SmartProperty.Application.Authentication.Register;
using SmartProperty.Domain.Identity;
using SmartProperty.UnitTests.TestDoubles;
using Xunit;

namespace SmartProperty.UnitTests.Application.Authentication;

/// <summary>
/// Exercises the real register handler for the phone step. By default the phone gets a code by SMS; with
/// auto-verification on, the phone is verified at registration and nothing is sent. The email is verified by code in
/// both cases.
/// </summary>
public sealed class RegisterCommandHandlerTests
{
    private const string Email = "new.user@example.test";
    private const string Phone = "+201012345678";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeUserRepository _users = new();
    private readonly FakeUserCredentialRepository _credentials = new();
    private readonly FakeOneTimeCodeRepository _codes = new();
    private readonly FakeSmsSender _sms = new();
    private readonly FakeEmailSender _email = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    [Fact]
    public async Task ByDefault_SendsAPhoneCodeAndLeavesThePhoneUnverified()
    {
        var result = await Register(autoVerifyPhone: false);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.PhoneVerificationExpiresAt);
        Assert.Single(_sms.Sent);
        Assert.Contains(_codes.All, code => code.Purpose == OneTimeCodePurpose.PhoneVerification);

        var user = await _users.GetByEmailAsync(Email);
        Assert.False(user!.IsPhoneVerified);
        Assert.Equal(UserStatus.Pending, user.Status);
    }

    [Fact]
    public async Task WithAutoVerify_TheStatusStaysPendingAndThePhoneIsVerifiedWithoutAnSms()
    {
        var result = await Register(autoVerifyPhone: true);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.PhoneVerificationExpiresAt);
        Assert.Empty(_sms.Sent);
        Assert.DoesNotContain(_codes.All, code => code.Purpose == OneTimeCodePurpose.PhoneVerification);

        var user = await _users.GetByEmailAsync(Email);
        Assert.True(user!.IsPhoneVerified);
        Assert.Equal(Now, user.PhoneVerifiedAt);

        // The email still has to be proved, so the account is not active yet.
        Assert.False(user.IsEmailVerified);
        Assert.Equal(UserStatus.Pending, user.Status);
    }

    [Fact]
    public async Task WithAutoVerify_TheEmailStillGetsACode()
    {
        var result = await Register(autoVerifyPhone: true);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now.Add(OneTimeCodePolicy.CodeLifetime), result.Value.EmailVerificationExpiresAt);
        Assert.Single(_email.Sent);
        Assert.Equal(Email, _email.Sent[0].ToEmail);
        Assert.Contains(_codes.All, code => code.Purpose == OneTimeCodePurpose.EmailVerification);
    }

    private Task<SmartProperty.Common.Results.Result<RegisterResult>> Register(bool autoVerifyPhone)
    {
        var handler = new RegisterCommandHandler(
            _users,
            _credentials,
            _codes,
            new FakePasswordHasher(),
            _sms,
            _email,
            new StubPolicy(autoVerifyPhone),
            new FakeDateTimeProvider(Now),
            _unitOfWork);

        return handler.Handle(new RegisterCommand(Email, "a-password", "New", "User", Phone));
    }

    private sealed class StubPolicy(bool autoVerifyPhone) : IContactVerificationPolicy
    {
        public bool AutoVerifyPhone { get; } = autoVerifyPhone;
    }
}
