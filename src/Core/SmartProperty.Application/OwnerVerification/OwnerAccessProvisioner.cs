using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Abstractions.Time;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.OwnerVerification;

/// <summary>
/// Grants and withdraws the right to add properties by assigning the "Property Owner" platform role: a role holding
/// only <c>property.create</c> and <c>property.read</c>, so the existing permission check is the one gate and owners
/// can neither update nor archive properties. The one exception is the photos of a property they own, which
/// <see cref="PropertyImages.PropertyImageAccess"/> allows on the strength of that ownership plus this role.
/// </summary>
/// <remarks>
/// The role and its permissions are created on first use, so no bootstrap step is needed. Everything is tracked only;
/// the caller commits, and retries once if a concurrent first use wins the unique constraints.
/// </remarks>
public sealed class OwnerAccessProvisioner(
    IPermissionRepository permissionRepository,
    IRoleRepository roleRepository,
    IDateTimeProvider dateTimeProvider)
{
    public const string PropertyOwnerRoleName = "Property Owner";

    private static readonly (string Code, string Description)[] OwnerPermissions =
    [
        (PermissionCodes.PropertyCreate, PermissionCodes.PropertyCreateDescription),
        (PermissionCodes.PropertyRead, PermissionCodes.PropertyReadDescription),
    ];

    public async Task GrantAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var permissions = new List<Permission>(OwnerPermissions.Length);

        foreach (var (code, description) in OwnerPermissions)
        {
            var permission = await permissionRepository.GetByCodeAsync(code, cancellationToken);

            if (permission is null)
            {
                permission = new Permission(Guid.NewGuid(), code, description);
                await permissionRepository.AddAsync(permission, cancellationToken);
            }

            permissions.Add(permission);
        }

        var role = await roleRepository.GetPlatformRoleByNameAsync(PropertyOwnerRoleName, cancellationToken);
        var roleCreated = role is null;

        if (role is null)
        {
            role = new Role(
                Guid.NewGuid(),
                PropertyOwnerRoleName,
                RoleScope.Platform,
                workspaceId: null,
                dateTimeProvider.UtcNow);

            await roleRepository.AddAsync(role, cancellationToken);
        }

        foreach (var permission in permissions)
        {
            // A role created a moment ago in this unit of work cannot be queried by id yet, and cannot have the row.
            if (roleCreated || !await roleRepository.HasPermissionAsync(role.Id, permission.Id, cancellationToken))
            {
                await roleRepository.AddPermissionAsync(new RolePermission(role.Id, permission.Id), cancellationToken);
            }
        }

        if (roleCreated || !await roleRepository.IsAssignedToUserAsync(userId, role.Id, cancellationToken))
        {
            await roleRepository.AddUserAssignmentAsync(new UserPlatformRole(userId, role.Id), cancellationToken);
        }
    }

    public async Task RevokeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var role = await roleRepository.GetPlatformRoleByNameAsync(PropertyOwnerRoleName, cancellationToken);

        if (role is not null)
        {
            await roleRepository.RemoveUserAssignmentAsync(userId, role.Id, cancellationToken);
        }
    }
}
