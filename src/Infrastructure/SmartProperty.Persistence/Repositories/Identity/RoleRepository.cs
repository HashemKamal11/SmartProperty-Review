using Microsoft.EntityFrameworkCore;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.Repositories.Identity;

internal sealed class RoleRepository(ApplicationDbContext dbContext) : IRoleRepository
{
    public Task<Role?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(id, nameof(id));

        return dbContext.Roles.FirstOrDefaultAsync(role => role.Id == id, cancellationToken);
    }

    public Task<Role?> GetPlatformRoleByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Role name must not be empty.", nameof(name));
        }

        // Trimmed to match how Role stores its name. Scope and workspace id are both constrained, so a workspace
        // role sharing the name can never be returned even if the two constraints disagree in the data.
        var normalizedName = name.Trim();

        return dbContext.Roles.FirstOrDefaultAsync(
            role => role.Name == normalizedName
                && role.Scope == RoleScope.Platform
                && role.WorkspaceId == null,
            cancellationToken);
    }

    public async Task AddAsync(Role role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        await dbContext.Roles.AddAsync(role, cancellationToken);
    }

    public Task<bool> HasPermissionAsync(
        Guid roleId,
        Guid permissionId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(roleId, nameof(roleId));
        ValidateRequiredId(permissionId, nameof(permissionId));

        return dbContext.RolePermissions.AnyAsync(
            rolePermission => rolePermission.RoleId == roleId && rolePermission.PermissionId == permissionId,
            cancellationToken);
    }

    public async Task AddPermissionAsync(
        RolePermission rolePermission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rolePermission);

        await dbContext.RolePermissions.AddAsync(rolePermission, cancellationToken);
    }

    public Task<bool> IsAssignedToUserAsync(
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredId(userId, nameof(userId));
        ValidateRequiredId(roleId, nameof(roleId));

        return dbContext.UserPlatformRoles.AnyAsync(
            assignment => assignment.UserId == userId && assignment.RoleId == roleId,
            cancellationToken);
    }

    public async Task AddUserAssignmentAsync(
        UserPlatformRole userPlatformRole,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userPlatformRole);

        await dbContext.UserPlatformRoles.AddAsync(userPlatformRole, cancellationToken);
    }

    private static void ValidateRequiredId(Guid id, string parameterName)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", parameterName);
        }
    }
}
