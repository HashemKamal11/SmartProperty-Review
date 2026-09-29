using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// In-memory roles together with the two assignment tables that hang off them, so a bootstrap test can assert
/// exactly which rows a run would have written.
/// </summary>
/// <remarks>
/// The name lookup restates the real repository's predicates — platform scope and a null workspace id — so a
/// workspace role sharing the name is invisible here too.
/// </remarks>
internal sealed class FakeRoleRepository : IRoleRepository
{
    private readonly Dictionary<Guid, Role> _rolesById = [];
    private readonly List<Role> _addedRoles = [];
    private readonly List<RolePermission> _rolePermissions = [];
    private readonly List<RolePermission> _addedRolePermissions = [];
    private readonly List<UserPlatformRole> _userAssignments = [];
    private readonly List<UserPlatformRole> _addedUserAssignments = [];

    public IReadOnlyList<Role> AddedRoles => _addedRoles;

    public IReadOnlyList<RolePermission> AddedRolePermissions => _addedRolePermissions;

    public IReadOnlyList<UserPlatformRole> AddedUserAssignments => _addedUserAssignments;

    /// <summary>Every role/permission row that would exist after the run, seeded or added.</summary>
    public IReadOnlyList<RolePermission> RolePermissions => _rolePermissions;

    /// <summary>Every platform role assignment that would exist after the run, seeded or added.</summary>
    public IReadOnlyList<UserPlatformRole> UserAssignments => _userAssignments;

    public void Seed(Role role)
    {
        _rolesById[role.Id] = role;
    }

    public void Seed(RolePermission rolePermission)
    {
        _rolePermissions.Add(rolePermission);
    }

    public void Seed(UserPlatformRole userPlatformRole)
    {
        _userAssignments.Add(userPlatformRole);
    }

    public Task<Role?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_rolesById.GetValueOrDefault(id));
    }

    public Task<Role?> GetPlatformRoleByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalizedName = name.Trim();

        return Task.FromResult(_rolesById.Values.FirstOrDefault(
            role => string.Equals(role.Name, normalizedName, StringComparison.Ordinal)
                && role.Scope == RoleScope.Platform
                && role.WorkspaceId is null));
    }

    public Task AddAsync(Role role, CancellationToken cancellationToken = default)
    {
        _addedRoles.Add(role);
        Seed(role);

        return Task.CompletedTask;
    }

    public Task<bool> HasPermissionAsync(
        Guid roleId,
        Guid permissionId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_rolePermissions.Any(
            rolePermission => rolePermission.RoleId == roleId && rolePermission.PermissionId == permissionId));
    }

    public Task AddPermissionAsync(RolePermission rolePermission, CancellationToken cancellationToken = default)
    {
        _addedRolePermissions.Add(rolePermission);
        Seed(rolePermission);

        return Task.CompletedTask;
    }

    public Task<bool> IsAssignedToUserAsync(
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_userAssignments.Any(
            assignment => assignment.UserId == userId && assignment.RoleId == roleId));
    }

    public Task AddUserAssignmentAsync(
        UserPlatformRole userPlatformRole,
        CancellationToken cancellationToken = default)
    {
        _addedUserAssignments.Add(userPlatformRole);
        Seed(userPlatformRole);

        return Task.CompletedTask;
    }
}
