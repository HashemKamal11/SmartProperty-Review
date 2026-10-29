using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.Authentication.Register;

/// <summary>
/// Outcome of a successful registration. Carries identifiers and statuses only; no credentials or tokens.
/// The phone number and email are not verified yet: the two expiry times are when the codes just sent to them
/// stop working.
/// </summary>
public sealed record RegisterResult(
    Guid UserId,
    UserStatus UserStatus,
    Guid WorkspaceId,
    Guid WorkspaceAccessRequestId,
    WorkspaceAccessRequestStatus WorkspaceAccessRequestStatus,
    DateTimeOffset PhoneVerificationExpiresAt,
    DateTimeOffset EmailVerificationExpiresAt);
