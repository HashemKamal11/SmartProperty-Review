using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Pagination;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Workspaces.Administration.List;

public sealed class GetAdminWorkspacesQueryHandler(IWorkspaceRepository workspaces)
    : IQueryHandler<GetAdminWorkspacesQuery, PagedList<AdminWorkspaceDetails>>
{
    public async Task<Result<PagedList<AdminWorkspaceDetails>>> Handle(GetAdminWorkspacesQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageParameters(query.Page ?? 1, query.PageSize ?? 10);
        var result = await workspaces.ListAdministrativeAsync(page.Offset, page.PageSize, cancellationToken);
        return Result<PagedList<AdminWorkspaceDetails>>.Success(
            PagedList<AdminWorkspaceDetails>.Create(result.Items, page.PageNumber, page.PageSize, result.TotalCount));
    }
}