using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceAccessRequests.List;

/// <summary>
/// One row of the review queue: what an administrator needs to decide a request, and nothing more.
/// </summary>
/// <remarks>
/// A read model rather than an entity. Persistence projects straight into it in one query, which is what keeps
/// the applicant's name and the workspace's name available without a lookup per row. It carries no credential,
/// token, role, or permission data.
/// </remarks>
public sealed record WorkspaceAccessRequestListItem(
    Guid RequestId,
    Guid UserId,
    string UserEmail,
    string UserFirstName,
    string UserLastName,
    Guid WorkspaceId,
    string WorkspaceName,
    WorkspaceAccessRequestStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? ReviewedAt,
    Guid? ReviewedByUserId);
