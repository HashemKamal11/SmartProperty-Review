using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.OneTimeCodes;

/// <summary>
/// Failures shared by the code flows. The code prefix names the flow, so a client can tell the flows apart.
/// </summary>
internal static class OneTimeCodeErrors
{
    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }

    // One answer for a wrong code, an expired code, and a missing code, so the response does not say which.
    public static Error InvalidOrExpired(OneTimeCodePurpose purpose)
    {
        return new Error(
            $"{Prefix(purpose)}.invalid_code",
            $"The {Noun(purpose)} is invalid or has expired.",
            ErrorType.Validation);
    }

    // One answer for an unknown email, a disabled account, a wrong token, an expired token and a token already used.
    public static Error InvalidResetToken { get; } = new(
        "password_reset.invalid_token",
        "The password reset session is invalid or has expired. Start again with your email.",
        ErrorType.Validation);

    public static Error TooManyAttempts(OneTimeCodePurpose purpose)
    {
        return new Error(
            $"{Prefix(purpose)}.too_many_attempts",
            $"Too many incorrect attempts. Request a new {Noun(purpose)}.",
            ErrorType.Conflict);
    }

    public static Error ResendTooSoon(OneTimeCodePurpose purpose)
    {
        return new Error(
            $"{Prefix(purpose)}.resend_too_soon",
            $"A {Noun(purpose)} was sent recently. Wait a moment before requesting another.",
            ErrorType.Conflict);
    }

    public static Error ForCheck(OneTimeCodePurpose purpose, CodeCheck check)
    {
        return check == CodeCheck.TooManyAttempts ? TooManyAttempts(purpose) : InvalidOrExpired(purpose);
    }

    private static string Prefix(OneTimeCodePurpose purpose)
    {
        return purpose switch
        {
            OneTimeCodePurpose.PhoneVerification => "phone_verification",
            OneTimeCodePurpose.EmailVerification => "email_verification",
            _ => "password_reset"
        };
    }

    private static string Noun(OneTimeCodePurpose purpose)
    {
        return purpose == OneTimeCodePurpose.PasswordReset ? "reset code" : "verification code";
    }
}
