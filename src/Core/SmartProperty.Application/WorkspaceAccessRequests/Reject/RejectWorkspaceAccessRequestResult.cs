using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceAccessRequests.Reject;

/// <summary>
/// Outcome of a successful rejection. <see cref="UserStatus"/> is reported unchanged, which is the point: a
/// rejection decides the request and never touches the account.
/// </summary>
public sealed record RejectWorkspaceAccessRequestResult(
    Guid RequestId,
    Guid UserId,
    Guid WorkspaceId,
    WorkspaceAccessRequestStatus RequestStatus,
    UserStatus UserStatus);
