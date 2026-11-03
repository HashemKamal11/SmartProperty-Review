using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.Register;

/// <summary>
/// Outcome of a successful registration. Carries identifiers and statuses only; no credentials or tokens.
/// The account is Pending: it cannot sign in until the phone number and the email are both verified, which
/// activates it. The two expiry times are when the codes just sent to them stop working.
/// </summary>
public sealed record RegisterResult(
    Guid UserId,
    UserStatus UserStatus,
    DateTimeOffset PhoneVerificationExpiresAt,
    DateTimeOffset EmailVerificationExpiresAt);
