using SmartProperty.Domain.Identity;

namespace SmartProperty.Application.Abstractions.Persistence.Repositories;

/// <summary>
/// Roles and the two kinds of assignment that hang off them: which permissions a role carries, and which users
/// hold it at platform scope.
/// </summary>
/// <remarks>
/// The assignment members exist for provisioning — creating a role and wiring it up — not for authorization.
/// Resolving whether a user holds a permission stays behind <c>IPermissionChecker</c>, which answers it in one
/// query; see docs/authorization-model.md.
/// </remarks>
public interface IRoleRepository
{
    Task<Role?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The platform-scoped role with this name, if one exists. Only platform roles are considered, so a workspace
    /// role that happens to share the name can never be returned.
    /// </summary>
    Task<Role?> GetPlatformRoleByNameAsync(string name, CancellationToken cancellationToken = default);

    Task AddAsync(Role role, CancellationToken cancellationToken = default);

    /// <summary>True when this role already carries this permission.</summary>
    Task<bool> HasPermissionAsync(
        Guid roleId,
        Guid permissionId,
        CancellationToken cancellationToken = default);

    Task AddPermissionAsync(RolePermission rolePermission, CancellationToken cancellationToken = default);

    /// <summary>True when this user already holds this role at platform scope.</summary>
    Task<bool> IsAssignedToUserAsync(
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken = default);

    Task AddUserAssignmentAsync(UserPlatformRole userPlatformRole, CancellationToken cancellationToken = default);
}
