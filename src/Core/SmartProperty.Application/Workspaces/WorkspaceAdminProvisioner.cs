using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.Application.Workspaces;

/// <summary>
/// Makes an account the admin of a workspace that is being created: a new <see cref="WorkspaceRoles.AdminRoleName"/>
/// role in that workspace carrying exactly workspace.read and workspace.members.create, a membership, and the
/// assignment. Used when a platform administrator creates a workspace with its admin and when a workspace
/// registration request is approved, so both paths grant exactly the same thing.
/// </summary>
/// <remarks>Only adds to the unit of work; the caller saves once, together with the workspace.</remarks>
internal sealed class WorkspaceAdminProvisioner(
    IWorkspaceMembershipRepository memberships,
    IRoleRepository roles,
    IPermissionRepository permissions)
{
    /// <returns>The id of the new membership.</returns>
    public async Task<Guid> ProvisionAsync(Guid workspaceId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The admin reads the workspace and creates its member accounts; nothing else is granted.
        var adminPermissions = new[]
        {
            await GetOrAddPermissionAsync(PermissionCodes.WorkspaceRead, PermissionCodes.WorkspaceReadDescription, cancellationToken),
            await GetOrAddPermissionAsync(PermissionCodes.WorkspaceMembersCreate, PermissionCodes.WorkspaceMembersCreateDescription, cancellationToken),
        };
        var role = new Role(Guid.NewGuid(), WorkspaceRoles.AdminRoleName, RoleScope.Workspace, workspaceId, now);
        var membership = new WorkspaceMembership(Guid.NewGuid(), userId, workspaceId, now);

        await roles.AddAsync(role, cancellationToken);
        foreach (var permission in adminPermissions)
            await roles.AddPermissionAsync(new RolePermission(role.Id, permission.Id), cancellationToken);
        await memberships.AddAsync(membership, cancellationToken);
        await roles.AddMembershipAssignmentAsync(new WorkspaceMembershipRole(membership.Id, role.Id), cancellationToken);

        return membership.Id;
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
