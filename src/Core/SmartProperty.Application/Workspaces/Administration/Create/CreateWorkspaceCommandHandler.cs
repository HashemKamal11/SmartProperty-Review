using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.Register;
using SmartProperty.Application.Authorization;
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

            // The admin reads the workspace and creates its member accounts; nothing else is granted.
            var adminPermissions = new[]
            {
                await GetOrAddPermissionAsync(PermissionCodes.WorkspaceRead, PermissionCodes.WorkspaceReadDescription, cancellationToken),
                await GetOrAddPermissionAsync(PermissionCodes.WorkspaceMembersCreate, PermissionCodes.WorkspaceMembersCreateDescription, cancellationToken),
            };
            var role = new Role(Guid.NewGuid(), WorkspaceRoles.AdminRoleName, RoleScope.Workspace, workspace.Id, now);
            var membership = new WorkspaceMembership(Guid.NewGuid(), user.Id, workspace.Id, now);

            await workspaces.AddAsync(workspace, cancellationToken);
            await users.AddAsync(user, cancellationToken);
            await credentials.AddAsync(new UserCredential(user.Id, passwordHasher.Hash(admin.Password!), now), cancellationToken);
            await roles.AddAsync(role, cancellationToken);
            foreach (var permission in adminPermissions)
                await roles.AddPermissionAsync(new RolePermission(role.Id, permission.Id), cancellationToken);
            await memberships.AddAsync(membership, cancellationToken);
            await roles.AddMembershipAssignmentAsync(new WorkspaceMembershipRole(membership.Id, role.Id), cancellationToken);
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

    private async Task<Permission> GetOrAddPermissionAsync(string code, string description, CancellationToken cancellationToken)
    {
        var permission = await permissions.GetByCodeAsync(code, cancellationToken);
        if (permission is null)
        {
            permission = new Permission(Guid.NewGuid(), code, description);
            await permissions.AddAsync(permission, cancellationToken);
        }
        return permission;
    }
}
