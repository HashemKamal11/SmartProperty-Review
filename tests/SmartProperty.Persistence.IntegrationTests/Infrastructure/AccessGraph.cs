using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Context;

namespace SmartProperty.Persistence.IntegrationTests.Infrastructure;

/// <summary>
/// Builds the persisted rows the authorization and persistence tests need, using the real entity constructors
/// and the real <see cref="ApplicationDbContext"/>.
/// </summary>
/// <remarks>
/// Every helper writes exactly the rows it names and nothing else, so a test that omits one — a membership, a
/// role assignment, an activation — really is missing it in the database.
/// </remarks>
internal static class AccessGraph
{
    public static User ActiveUser(string email = "active.user@example.test")
    {
        var user = NewUser(email);
        user.Activate(TestClock.DefaultNow);

        return user;
    }

    /// <summary>A user left in the status <see cref="User"/>'s constructor assigns: <c>Pending</c>.</summary>
    public static User PendingUser(string email = "pending.user@example.test")
    {
        return NewUser(email);
    }

    public static User SuspendedUser(string email = "suspended.user@example.test")
    {
        var user = ActiveUser(email);
        user.Suspend(TestClock.DefaultNow);

        return user;
    }

    public static User DeactivatedUser(string email = "deactivated.user@example.test")
    {
        var user = ActiveUser(email);
        user.Deactivate(TestClock.DefaultNow);

        return user;
    }

    public static Workspace NewWorkspace(string name = "Test Workspace")
    {
        return new Workspace(Guid.NewGuid(), name, TestClock.DefaultNow);
    }

    public static Role NewPlatformRole(string name = "Platform Role")
    {
        return new Role(Guid.NewGuid(), name, RoleScope.Platform, workspaceId: null, TestClock.DefaultNow);
    }

    public static Role NewWorkspaceRole(Guid workspaceId, string name = "Workspace Role")
    {
        return new Role(Guid.NewGuid(), name, RoleScope.Workspace, workspaceId, TestClock.DefaultNow);
    }

    public static Permission NewPermission(string code)
    {
        return new Permission(Guid.NewGuid(), code);
    }

    public static WorkspaceMembership NewMembership(Guid userId, Guid workspaceId)
    {
        return new WorkspaceMembership(Guid.NewGuid(), userId, workspaceId, TestClock.DefaultNow);
    }

    public static RefreshToken ActiveRefreshToken(Guid userId, string tokenHash)
    {
        return new RefreshToken(
            Guid.NewGuid(),
            userId,
            tokenHash,
            TestClock.DefaultNow,
            TestClock.DefaultNow.AddDays(7));
    }

    /// <summary>
    /// Persists a complete platform grant: an active user holding a platform role that carries the permission.
    /// </summary>
    public static async Task<(User User, Permission Permission)> SeedPlatformGrantAsync(
        ApplicationDbContext context,
        string permissionCode)
    {
        var user = ActiveUser();
        var role = NewPlatformRole();
        var permission = NewPermission(permissionCode);

        context.Users.Add(user);
        context.Roles.Add(role);
        context.Permissions.Add(permission);
        context.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
        context.UserPlatformRoles.Add(new UserPlatformRole(user.Id, role.Id));

        await context.SaveChangesAsync();

        return (user, permission);
    }

    /// <summary>
    /// Persists a complete workspace grant: an active user, a membership in the workspace, and a role scoped to
    /// that same workspace carrying the permission.
    /// </summary>
    public static async Task<(User User, Workspace Workspace, Permission Permission)> SeedWorkspaceGrantAsync(
        ApplicationDbContext context,
        string permissionCode)
    {
        var user = ActiveUser();
        var workspace = NewWorkspace();
        var permission = NewPermission(permissionCode);

        context.Users.Add(user);
        context.Workspaces.Add(workspace);
        context.Permissions.Add(permission);

        await GrantInWorkspaceAsync(context, user, workspace, permission);

        return (user, workspace, permission);
    }

    /// <summary>
    /// Adds the membership, workspace role, and role/permission rows that let <paramref name="user"/> exercise
    /// <paramref name="permission"/> inside <paramref name="workspace"/>. The user, workspace, and permission
    /// rows must already be tracked or persisted.
    /// </summary>
    public static async Task GrantInWorkspaceAsync(
        ApplicationDbContext context,
        User user,
        Workspace workspace,
        Permission permission)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(permission);

        var membership = NewMembership(user.Id, workspace.Id);
        var role = NewWorkspaceRole(workspace.Id);

        context.WorkspaceMemberships.Add(membership);
        context.Roles.Add(role);
        context.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
        context.WorkspaceMembershipRoles.Add(new WorkspaceMembershipRole(membership.Id, role.Id));

        await context.SaveChangesAsync();
    }

    private static User NewUser(string email)
    {
        return new User(Guid.NewGuid(), email, "Test", "User", TestClock.DefaultNow);
    }
}
