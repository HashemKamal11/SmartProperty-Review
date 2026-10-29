namespace SmartProperty.Application.Authentication.PhoneVerifications;

/// <summary>
/// Outcome of sending a verification code. Never carries the code or the phone number.
/// </summary>
public sealed record PhoneVerificationIssued(DateTimeOffset ExpiresAt);
