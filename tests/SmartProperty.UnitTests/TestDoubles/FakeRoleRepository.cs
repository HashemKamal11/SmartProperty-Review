using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;

namespace SmartProperty.UnitTests.TestDoubles;

/// <summary>
/// In-memory roles together with the three assignment tables that hang off them — role permissions, platform user
/// roles, and workspace membership roles — so a bootstrap or approval test can assert exactly which rows a run
/// would have written.
/// </summary>
/// <remarks>
/// Both name lookups restate the real repository's predicates. The platform lookup requires platform scope and a
/// null workspace id; the workspace lookup requires workspace scope and that exact workspace id. A platform role,
/// or another workspace's role, sharing the name is therefore invisible here too.
/// </remarks>
internal sealed class FakeRoleRepository : IRoleRepository
{
    private readonly Dictionary<Guid, Role> _rolesById = [];
    private readonly List<Role> _addedRoles = [];
    private readonly List<RolePermission> _rolePermissions = [];
    private readonly List<RolePermission> _addedRolePermissions = [];
    private readonly List<UserPlatformRole> _userAssignments = [];
    private readonly List<UserPlatformRole> _addedUserAssignments = [];
    private readonly List<WorkspaceMembershipRole> _membershipAssignments = [];
    private readonly List<WorkspaceMembershipRole> _addedMembershipAssignments = [];

    public IReadOnlyList<Role> AddedRoles => _addedRoles;

    public IReadOnlyList<RolePermission> AddedRolePermissions => _addedRolePermissions;

    public IReadOnlyList<UserPlatformRole> AddedUserAssignments => _addedUserAssignments;

    public IReadOnlyList<WorkspaceMembershipRole> AddedMembershipAssignments => _addedMembershipAssignments;

    /// <summary>Every role/permission row that would exist after the run, seeded or added.</summary>
    public IReadOnlyList<RolePermission> RolePermissions => _rolePermissions;

    /// <summary>Every platform role assignment that would exist after the run, seeded or added.</summary>
    public IReadOnlyList<UserPlatformRole> UserAssignments => _userAssignments;

    /// <summary>Every workspace membership assignment that would exist after the run, seeded or added.</summary>
    public IReadOnlyList<WorkspaceMembershipRole> MembershipAssignments => _membershipAssignments;

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

    public void Seed(WorkspaceMembershipRole workspaceMembershipRole)
    {
        _membershipAssignments.Add(workspaceMembershipRole);
    }

    /// <summary>
    /// Forgets everything added since construction, as a rolled-back transaction would. Seeded rows stay.
    /// </summary>
    public void RollBackAdded()
    {
        foreach (var role in _addedRoles)
        {
            _rolesById.Remove(role.Id);
        }

        foreach (var rolePermission in _addedRolePermissions)
        {
            _rolePermissions.Remove(rolePermission);
        }

        foreach (var assignment in _addedUserAssignments)
        {
            _userAssignments.Remove(assignment);
        }

        foreach (var assignment in _addedMembershipAssignments)
        {
            _membershipAssignments.Remove(assignment);
        }

        _addedRoles.Clear();
        _addedRolePermissions.Clear();
        _addedUserAssignments.Clear();
        _addedMembershipAssignments.Clear();
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

    public Task<Role?> GetWorkspaceRoleByNameAsync(
        Guid workspaceId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = name.Trim();

        return Task.FromResult(_rolesById.Values.FirstOrDefault(
            role => string.Equals(role.Name, normalizedName, StringComparison.Ordinal)
                && role.Scope == RoleScope.Workspace
                && role.WorkspaceId == workspaceId));
    }

    public Task<bool> IsAssignedToMembershipAsync(
        Guid workspaceMembershipId,
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_membershipAssignments.Any(
            assignment => assignment.WorkspaceMembershipId == workspaceMembershipId && assignment.RoleId == roleId));
    }

    public Task AddMembershipAssignmentAsync(
        WorkspaceMembershipRole workspaceMembershipRole,
        CancellationToken cancellationToken = default)
    {
        _addedMembershipAssignments.Add(workspaceMembershipRole);
        Seed(workspaceMembershipRole);

        return Task.CompletedTask;
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
