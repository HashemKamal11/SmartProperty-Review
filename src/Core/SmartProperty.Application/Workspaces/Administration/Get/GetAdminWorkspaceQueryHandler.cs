using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Workspaces.Administration.Get;

public sealed class GetAdminWorkspaceQueryHandler(IWorkspaceRepository workspaces)
    : IQueryHandler<GetAdminWorkspaceQuery, AdminWorkspaceDetails>
{
    public async Task<Result<AdminWorkspaceDetails>> Handle(GetAdminWorkspaceQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var workspace = query.WorkspaceId == Guid.Empty ? null : await workspaces.GetByIdAsync(query.WorkspaceId, cancellationToken);
        return workspace is null
            ? Result<AdminWorkspaceDetails>.Failure(AdminWorkspaceErrors.NotFound)
            : Result<AdminWorkspaceDetails>.Success(new(workspace.Id, workspace.Name, workspace.CreatedAt, workspace.UpdatedAt));
    }
}