using SmartProperty.Application.Abstractions.Authentication;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Abstractions.Messaging;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authentication.Register;
using SmartProperty.Application.Authorization;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.Workspaces.Members.Create;

/// <summary>
/// Creates an account for a new member of a workspace: an Active user with a credential, a membership in the
/// workspace, and the workspace's default <see cref="WorkspaceRoles.MemberRoleName"/> role, which carries exactly
/// <see cref="PermissionCodes.WorkspaceRead"/>. Everything is saved with one
/// <see cref="IUnitOfWork.SaveChangesAsync"/> call, so a half-created member is never left behind.
/// </summary>
/// <remarks>
/// The administrator vouches for the account, so it is active at once and needs no verification or approval.
/// The Member role is created with the workspace's first member if it does not exist yet. An existing Member role
/// is used only if it carries exactly workspace.read; any other permission set fails closed, as approval does,
/// so this endpoint can never hand out more than a member's access.
/// </remarks>
public sealed class CreateWorkspaceMemberCommandHandler(
    IAccessContextReader accessContext,
    IWorkspaceRepository workspaces,
    IUserRepository users,
    IUserCredentialRepository credentials,
    IWorkspaceMembershipRepository memberships,
    IRoleRepository roles,
    IPermissionRepository permissions,
    IPasswordHasher passwordHasher,
    IDateTimeProvider clock,
    IUnitOfWork unitOfWork)
    : ICommandHandler<CreateWorkspaceMemberCommand, CreateWorkspaceMemberResult>
{
    public async Task<Result<CreateWorkspaceMemberResult>> Handle(
        CreateWorkspaceMemberCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.AdminUserId == Guid.Empty)
            return Failure(CreateWorkspaceMemberErrors.NotAWorkspaceAdmin);

        if (NewAccountValidator.Validate(command.Email, command.Password, command.FirstName, command.LastName) is { } invalid)
            return Failure(CreateWorkspaceMemberErrors.ValidationFailed(invalid.Description));

        // The workspace is the one the caller administers: where they hold workspace.members.create. Authorization is
        // decided here, from persisted grants, and fails closed.
        var access = await accessContext.ReadAsync(command.AdminUserId, cancellationToken);
        var administered = access.Workspaces
            .Where(candidate => candidate.Permissions.Contains(PermissionCodes.WorkspaceMembersCreate, StringComparer.Ordinal))
            .ToList();
        if (administered.Count == 0)
            return Failure(CreateWorkspaceMemberErrors.NotAWorkspaceAdmin);
        if (administered.Count > 1)
            return Failure(CreateWorkspaceMemberErrors.AmbiguousWorkspace);

        var workspace = await workspaces.GetByIdAsync(administered[0].Id, cancellationToken);
        if (workspace is null)
            return Failure(CreateWorkspaceMemberErrors.WorkspaceNotFound);

        var now = clock.UtcNow;
        var user = new User(Guid.NewGuid(), command.Email!, command.FirstName!, command.LastName!, now);
        if (await users.GetByEmailAsync(user.Email, cancellationToken) is not null)
            return Failure(RegisterErrors.EmailAlreadyExists);
        user.Activate(now);

        var permission = await permissions.GetByCodeAsync(PermissionCodes.WorkspaceRead, cancellationToken);
        var role = await roles.GetWorkspaceRoleByNameAsync(workspace.Id, WorkspaceRoles.MemberRoleName, cancellationToken);

        var attachPermission = false;
        if (role is null)
        {
            role = new Role(Guid.NewGuid(), WorkspaceRoles.MemberRoleName, RoleScope.Workspace, workspace.Id, now);
            await roles.AddAsync(role, cancellationToken);
            attachPermission = true;
        }
        else
        {
            // Fail closed: reuse the role only if its complete permission set is exactly { workspace.read }.
            var granted = await roles.GetPermissionIdsAsync(role.Id, cancellationToken);
            if (permission is null || granted.Count != 1 || granted[0] != permission.Id)
                return Failure(CreateWorkspaceMemberErrors.MemberRoleMisconfigured);
        }

        if (permission is null)
        {
            permission = new Permission(Guid.NewGuid(), PermissionCodes.WorkspaceRead, PermissionCodes.WorkspaceReadDescription);
            await permissions.AddAsync(permission, cancellationToken);
        }

        var membership = new WorkspaceMembership(Guid.NewGuid(), user.Id, workspace.Id, now);

        await users.AddAsync(user, cancellationToken);
        await credentials.AddAsync(new UserCredential(user.Id, passwordHasher.Hash(command.Password!), now), cancellationToken);
        if (attachPermission)
            await roles.AddPermissionAsync(new RolePermission(role.Id, permission.Id), cancellationToken);
        await memberships.AddAsync(membership, cancellationToken);
        await roles.AddMembershipAssignmentAsync(new WorkspaceMembershipRole(membership.Id, role.Id), cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException exception) when (exception.Constraint == PersistenceConstraint.UserEmail)
        {
            // A concurrent registration took the email after the check above; the save was rejected as a whole.
            return Failure(RegisterErrors.EmailAlreadyExists);
        }
        catch (UniqueConstraintViolationException exception) when (
            exception.Constraint is PersistenceConstraint.WorkspaceRoleName
                or PersistenceConstraint.PermissionCode
                or PersistenceConstraint.RolePermissionKey)
        {
            return Failure(CreateWorkspaceMemberErrors.ConcurrentProvisioning);
        }

        return Result<CreateWorkspaceMemberResult>.Success(new(
            user.Id, membership.Id, workspace.Id, user.Email, user.FirstName, user.LastName, role.Name, now));
    }

    private static Result<CreateWorkspaceMemberResult> Failure(Error error) =>
        Result<CreateWorkspaceMemberResult>.Failure(error);
}
