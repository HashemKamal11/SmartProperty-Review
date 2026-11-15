using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.WorkspaceRegistrations.Review;

/// <summary>The review queue. <c>Status</c> defaults to Pending, the queue a reviewer wants.</summary>
public sealed record GetWorkspaceRegistrationsQuery(
    WorkspaceRegistrationRequestStatus? Status,
    int? Page,
    int? PageSize) : IQuery<PagedList<WorkspaceRegistrationView>>;

public sealed class GetWorkspaceRegistrationsQueryHandler(IWorkspaceRegistrationRequestRepository registrationRequests)
    : IQueryHandler<GetWorkspaceRegistrationsQuery, PagedList<WorkspaceRegistrationView>>
{
    public async Task<Result<PagedList<WorkspaceRegistrationView>>> Handle(
        GetWorkspaceRegistrationsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // PageParameters owns the bounds and the checked offset arithmetic.
        var pageParameters = new PageParameters();

        if (query.Page is { } page)
        {
            pageParameters.PageNumber = page;
        }

        if (query.PageSize is { } pageSize)
        {
            pageParameters.PageSize = pageSize;
        }

        var result = await registrationRequests.ListAsync(
            query.Status ?? WorkspaceRegistrationRequestStatus.Pending,
            pageParameters.Offset,
            pageParameters.PageSize,
            cancellationToken);

        return Result<PagedList<WorkspaceRegistrationView>>.Success(PagedList<WorkspaceRegistrationView>.Create(
            result.Items,
            pageParameters.PageNumber,
            pageParameters.PageSize,
            result.TotalCount));
    }
}

public sealed record GetWorkspaceRegistrationQuery(Guid Id) : IQuery<WorkspaceRegistrationView>;

public sealed class GetWorkspaceRegistrationQueryHandler(IWorkspaceRegistrationRequestRepository registrationRequests)
    : IQueryHandler<GetWorkspaceRegistrationQuery, WorkspaceRegistrationView>
{
    public async Task<Result<WorkspaceRegistrationView>> Handle(
        GetWorkspaceRegistrationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var view = query.Id == Guid.Empty ? null : await registrationRequests.GetViewAsync(query.Id, cancellationToken);

        return view is null
            ? Result<WorkspaceRegistrationView>.Failure(WorkspaceRegistrationErrors.NotFound)
            : Result<WorkspaceRegistrationView>.Success(view);
    }
}
