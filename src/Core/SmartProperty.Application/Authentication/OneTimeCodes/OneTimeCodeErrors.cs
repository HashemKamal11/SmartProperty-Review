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

    public static Error UserNotFound(OneTimeCodePurpose purpose)
    {
        return new Error($"{Prefix(purpose)}.user_not_found", "The account was not found.", ErrorType.NotFound);
    }

    public static Error AlreadyVerified(OneTimeCodePurpose purpose)
    {
        var what = purpose == OneTimeCodePurpose.PhoneVerification ? "phone number" : "email";

        return new Error($"{Prefix(purpose)}.already_verified", $"This {what} is already verified.", ErrorType.Conflict);
    }

    // One answer for a wrong code, an expired code, and a missing code, so the response does not say which.
    public static Error InvalidOrExpired(OneTimeCodePurpose purpose)
    {
        return new Error(
            $"{Prefix(purpose)}.invalid_code",
            $"The {Noun(purpose)} is invalid or has expired.",
            ErrorType.Validation);
    }

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
