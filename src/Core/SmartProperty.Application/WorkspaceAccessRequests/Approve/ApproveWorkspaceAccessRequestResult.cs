using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceAccessRequests.Approve;

/// <summary>
/// Outcome of a successful approval: identifiers and resulting statuses only, no credentials, tokens, or
/// personal data.
/// </summary>
public sealed record ApproveWorkspaceAccessRequestResult(
    Guid RequestId,
    Guid UserId,
    Guid WorkspaceId,
    WorkspaceAccessRequestStatus RequestStatus,
    UserStatus UserStatus,
    Guid MembershipId);
