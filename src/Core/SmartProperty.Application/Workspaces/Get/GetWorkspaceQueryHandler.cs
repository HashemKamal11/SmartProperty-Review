using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Common.Results;

namespace SmartProperty.Application.Workspaces.Get;

/// <summary>
/// Returns one workspace's id and name. Read-only: it saves nothing.
/// </summary>
/// <remarks>
/// A missing workspace is a <see cref="ErrorType.Forbidden"/> failure, not a not-found: the endpoint is protected by
/// a workspace permission that a non-existent workspace can never satisfy, and the response must not reveal whether
/// a workspace exists. Normally the authorization filter has already answered; this is the fail-closed second layer.
/// </remarks>
public sealed class GetWorkspaceQueryHandler(IWorkspaceRepository workspaceRepository)
    : IQueryHandler<GetWorkspaceQuery, WorkspaceDetails>
{
    public async Task<Result<WorkspaceDetails>> Handle(
        GetWorkspaceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var workspace = query.WorkspaceId == Guid.Empty
            ? null
            : await workspaceRepository.GetByIdAsync(query.WorkspaceId, cancellationToken);

        return workspace is null
            ? Result<WorkspaceDetails>.Failure(GetWorkspaceErrors.Forbidden)
            : Result<WorkspaceDetails>.Success(new WorkspaceDetails(workspace.Id, workspace.Name));
    }
}
