using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.PhoneVerifications;

/// <summary>
/// Outcome of a successful phone verification. Carries no code, phone number, credentials, or tokens.
/// </summary>
public sealed record PhoneVerified(Guid UserId, DateTimeOffset VerifiedAt, UserStatus UserStatus);
