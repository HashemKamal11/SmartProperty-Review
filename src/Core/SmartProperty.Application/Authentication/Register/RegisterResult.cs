using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.Authentication.Register;

/// <summary>
/// Outcome of a successful registration. Carries identifiers and statuses only; no credentials or tokens.
/// </summary>
public sealed record RegisterResult(
    Guid UserId,
    UserStatus UserStatus,
    Guid WorkspaceId,
    Guid WorkspaceAccessRequestId,
    WorkspaceAccessRequestStatus WorkspaceAccessRequestStatus);
