using SmartProperty.Common.Results;

namespace SmartProperty.Application.Authentication.PhoneVerifications;

internal static class PhoneVerificationErrors
{
    public static readonly Error UserNotFound = new(
        "phone_verification.user_not_found",
        "The account was not found.",
        ErrorType.NotFound);

    public static readonly Error AlreadyVerified = new(
        "phone_verification.already_verified",
        "This phone number is already verified.",
        ErrorType.Conflict);

    // One answer for a wrong code, an expired code, and a missing code, so the response does not say which.
    public static readonly Error InvalidOrExpiredCode = new(
        "phone_verification.invalid_code",
        "The verification code is invalid or has expired.",
        ErrorType.Validation);

    public static readonly Error TooManyAttempts = new(
        "phone_verification.too_many_attempts",
        "Too many incorrect attempts. Request a new verification code.",
        ErrorType.Conflict);

    public static readonly Error ResendTooSoon = new(
        "phone_verification.resend_too_soon",
        "A verification code was sent recently. Wait a moment before requesting another.",
        ErrorType.Conflict);

    public static Error ValidationFailed(string description)
    {
        return new Error("validation.failed", description, ErrorType.Validation);
    }
}
