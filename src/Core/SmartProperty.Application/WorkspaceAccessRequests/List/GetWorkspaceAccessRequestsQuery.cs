using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Common.Pagination;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceAccessRequests.List;

/// <summary>
/// Reads the workspace access request review queue.
/// </summary>
/// <remarks>
/// Deliberately narrow: an optional workspace, an optional status, and the standard page parameters. It is not a
/// general query language — no free-text search, no sort expression, no field selection.
/// </remarks>
/// <param name="WorkspaceId">Restrict to one workspace. Null means every workspace.</param>
/// <param name="Status">
/// Restrict to one status. Null means <see cref="WorkspaceAccessRequestStatus.Pending"/>, because the queue's
/// reason to exist is finding the requests that still need a decision.
/// </param>
/// <param name="Page">One-based page number. Null and out-of-range values fall back to <see cref="PageParameters"/>.</param>
/// <param name="PageSize">Page size, clamped by <see cref="PageParameters"/>.</param>
public sealed record GetWorkspaceAccessRequestsQuery(
    Guid? WorkspaceId,
    WorkspaceAccessRequestStatus? Status,
    int? Page,
    int? PageSize) : IQuery<PagedList<WorkspaceAccessRequestListItem>>;
