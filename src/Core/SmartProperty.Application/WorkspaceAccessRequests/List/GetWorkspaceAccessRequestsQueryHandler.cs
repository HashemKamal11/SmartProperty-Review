using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceAccessRequests.List;

/// <summary>
/// Returns one page of the review queue. Read-only: it saves nothing and reviews nothing.
/// </summary>
/// <remarks>
/// It exists because approve and reject need a request id, and until this query there was no way for an
/// administrator to discover one.
///
/// Defaulting and clamping happen here rather than at the API boundary, so every caller of the use case gets the
/// same bounded page regardless of what it passed.
/// </remarks>
public sealed class GetWorkspaceAccessRequestsQueryHandler(
    IWorkspaceAccessRequestRepository workspaceAccessRequestRepository)
    : IQueryHandler<GetWorkspaceAccessRequestsQuery, PagedList<WorkspaceAccessRequestListItem>>
{
    public async Task<Result<PagedList<WorkspaceAccessRequestListItem>>> Handle(
        GetWorkspaceAccessRequestsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // PageParameters owns both bounds and checked offset arithmetic, so no caller-controlled value can wrap
        // into a negative or multi-billion-row database offset.
        var pageParameters = new PageParameters();

        if (query.Page is { } page)
        {
            pageParameters.PageNumber = page;
        }

        if (query.PageSize is { } pageSize)
        {
            pageParameters.PageSize = pageSize;
        }

        // An empty workspace id would match nothing and is not a filter anyone can mean; treated as absent.
        var workspaceId = query.WorkspaceId is { } candidate && candidate != Guid.Empty ? candidate : (Guid?)null;

        var result = await workspaceAccessRequestRepository.ListAsync(
            query.Status ?? WorkspaceAccessRequestStatus.Pending,
            workspaceId,
            pageParameters.Offset,
            pageParameters.PageSize,
            cancellationToken);

        return Result<PagedList<WorkspaceAccessRequestListItem>>.Success(
            PagedList<WorkspaceAccessRequestListItem>.Create(
                result.Items,
                pageParameters.PageNumber,
                pageParameters.PageSize,
                result.TotalCount));
    }
}
