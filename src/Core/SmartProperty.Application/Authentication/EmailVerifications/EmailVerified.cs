using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Authentication.EmailVerifications;

/// <summary>
/// Outcome of a successful email verification. Carries no code, credentials, or tokens.
/// </summary>
public sealed record EmailVerified(Guid UserId, DateTimeOffset VerifiedAt, UserStatus UserStatus);
