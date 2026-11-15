using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.Register;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.Workspaces.Administration.Create;

/// <summary>
/// Creates a workspace and, when the command carries an admin account, the account that administers it. Everything
/// is written through one <see cref="IUnitOfWork.SaveChangesAsync"/> call, so a workspace is never left behind
/// without the admin that was requested with it.
/// </summary>
public sealed class CreateWorkspaceCommandHandler(
    IWorkspaceRepository workspaces,
    IUserRepository users,
    IUserCredentialRepository credentials,
    IWorkspaceMembershipRepository memberships,
    IRoleRepository roles,
    IPermissionRepository permissions,
    IPasswordHasher passwordHasher,
    IDateTimeProvider clock,
    IUnitOfWork unitOfWork)
    : ICommandHandler<CreateWorkspaceCommand, CreateWorkspaceResult>
{
    public async Task<Result<CreateWorkspaceResult>> Handle(CreateWorkspaceCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (CreateWorkspaceCommandValidator.Validate(command) is { } error)
            return Result<CreateWorkspaceResult>.Failure(error);

        var now = clock.UtcNow;
        var workspace = new Workspace(Guid.NewGuid(), command.Name!, now);

        Guid? adminUserId = null;
        if (command.Admin is { } admin)
        {
            var user = new User(Guid.NewGuid(), admin.Email!, admin.FirstName!, admin.LastName!, now);
            if (await users.GetByEmailAsync(user.Email, cancellationToken) is not null)
                return Result<CreateWorkspaceResult>.Failure(RegisterErrors.EmailAlreadyExists);
            // The platform admin vouches for this account, so it is active at once; there is no approval step.
            user.Activate(now);
            adminUserId = user.Id;

            await workspaces.AddAsync(workspace, cancellationToken);
            await users.AddAsync(user, cancellationToken);
            await credentials.AddAsync(new UserCredential(user.Id, passwordHasher.Hash(admin.Password!), now), cancellationToken);
            await new WorkspaceAdminProvisioner(memberships, roles, permissions)
                .ProvisionAsync(workspace.Id, user.Id, now, cancellationToken);
        }
        else
        {
            await workspaces.AddAsync(workspace, cancellationToken);
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException exception)
            when (adminUserId is not null && exception.Constraint == PersistenceConstraint.UserEmail)
        {
            // A concurrent registration took the email after the check above; the save was rejected as a whole.
            return Result<CreateWorkspaceResult>.Failure(RegisterErrors.EmailAlreadyExists);
        }

        return Result<CreateWorkspaceResult>.Success(
            new(workspace.Id, workspace.Name, workspace.CreatedAt, workspace.UpdatedAt, adminUserId));
    }
}
