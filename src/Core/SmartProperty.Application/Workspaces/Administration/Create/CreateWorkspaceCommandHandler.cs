using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.Workspaces.Administration.Create;

public sealed class CreateWorkspaceCommandHandler(IWorkspaceRepository workspaces, IDateTimeProvider clock, IUnitOfWork unitOfWork)
    : ICommandHandler<CreateWorkspaceCommand, AdminWorkspaceDetails>
{
    public async Task<Result<AdminWorkspaceDetails>> Handle(CreateWorkspaceCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (CreateWorkspaceCommandValidator.Validate(command) is { } error)
            return Result<AdminWorkspaceDetails>.Failure(error);
        var workspace = new Workspace(Guid.NewGuid(), command.Name!, clock.UtcNow);
        await workspaces.AddAsync(workspace, cancellationToken);
        // A single Workspace insert. No membership, role, request or grant is created.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<AdminWorkspaceDetails>.Success(new(workspace.Id, workspace.Name, workspace.CreatedAt, workspace.UpdatedAt));
    }
}