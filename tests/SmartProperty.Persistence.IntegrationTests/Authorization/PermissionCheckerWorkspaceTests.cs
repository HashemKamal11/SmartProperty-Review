using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Authorization;

/// <summary>
/// Resolves workspace permissions through the real <c>PermissionChecker</c> against persisted rows, and proves
/// a grant held in one workspace cannot answer a question about another.
/// </summary>
/// <remarks>
/// The workspace path needs a longer chain than the platform one — user, workspace_memberships,
/// workspace_membership_roles, roles scoped to that workspace, role_permissions, permissions — and the target
/// workspace is constrained on both the membership and the role. The isolation tests here remove or misdirect
/// exactly one link at a time, so each denial names the thing that actually prevents the bleed.
/// </remarks>
public sealed class PermissionCheckerWorkspaceTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string PermissionCode = "property.read";

    [Fact]
    public async Task ACompleteWorkspaceGrantIsAllowed()
    {
        await using var seeding = Host.CreateVerificationContext();
        var (user, workspace, _) = await AccessGraph.SeedWorkspaceGrantAsync(seeding, PermissionCode);

        Assert.Same(AuthorizationDecision.Allowed, await CheckAsync(user.Id, PermissionCode, workspace.Id));
    }

    [Fact]
    public async Task AGrantInOneWorkspaceDoesNotAnswerForAnother()
    {
        await using var seeding = Host.CreateVerificationContext();

        var (user, workspaceA, _) = await AccessGraph.SeedWorkspaceGrantAsync(seeding, PermissionCode);

        var workspaceB = AccessGraph.NewWorkspace("Workspace B");
        seeding.Workspaces.Add(workspaceB);
        await seeding.SaveChangesAsync();

        Assert.Same(AuthorizationDecision.Allowed, await CheckAsync(user.Id, PermissionCode, workspaceA.Id));
        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, PermissionCode, workspaceB.Id));
    }

    [Fact]
    public async Task AUserGrantedInBothWorkspacesIsAllowedInBoth()
    {
        // The counterpart to the isolation test: separating the workspaces must not deny a real second grant.
        await using var seeding = Host.CreateVerificationContext();

        var (user, workspaceA, permission) = await AccessGraph.SeedWorkspaceGrantAsync(seeding, PermissionCode);

        var workspaceB = AccessGraph.NewWorkspace("Workspace B");
        seeding.Workspaces.Add(workspaceB);
        await seeding.SaveChangesAsync();

        await AccessGraph.GrantInWorkspaceAsync(seeding, user, workspaceB, permission);

        Assert.Same(AuthorizationDecision.Allowed, await CheckAsync(user.Id, PermissionCode, workspaceA.Id));
        Assert.Same(AuthorizationDecision.Allowed, await CheckAsync(user.Id, PermissionCode, workspaceB.Id));
    }

    [Fact]
    public async Task AUserWithoutAMembershipIsDenied()
    {
        await using var seeding = Host.CreateVerificationContext();

        var user = AccessGraph.ActiveUser();
        var workspace = AccessGraph.NewWorkspace();
        var role = AccessGraph.NewWorkspaceRole(workspace.Id);
        var permission = AccessGraph.NewPermission(PermissionCode);

        seeding.Users.Add(user);
        seeding.Workspaces.Add(workspace);
        seeding.Roles.Add(role);
        seeding.Permissions.Add(permission);
        seeding.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
        await seeding.SaveChangesAsync();

        // The workspace role carries the permission; the user simply is not a member.
        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, PermissionCode, workspace.Id));
    }

    [Fact]
    public async Task AMembershipWithNoRoleAttachedIsDenied()
    {
        await using var seeding = Host.CreateVerificationContext();

        var user = AccessGraph.ActiveUser();
        var workspace = AccessGraph.NewWorkspace();
        var role = AccessGraph.NewWorkspaceRole(workspace.Id);
        var permission = AccessGraph.NewPermission(PermissionCode);

        seeding.Users.Add(user);
        seeding.Workspaces.Add(workspace);
        seeding.Roles.Add(role);
        seeding.Permissions.Add(permission);
        seeding.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
        seeding.WorkspaceMemberships.Add(AccessGraph.NewMembership(user.Id, workspace.Id));
        await seeding.SaveChangesAsync();

        // Only the workspace_membership_roles row is missing.
        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, PermissionCode, workspace.Id));
    }

    [Fact]
    public async Task AMembershipCarryingARoleFromAnotherWorkspaceIsDenied()
    {
        // An inconsistent association that the domain constructors would not produce but an import or a manual
        // fix could. The checker constrains the workspace on the role as well as on the membership, so it must
        // still fail closed.
        await using var seeding = Host.CreateVerificationContext();

        var user = AccessGraph.ActiveUser();
        var workspaceA = AccessGraph.NewWorkspace("Workspace A");
        var workspaceB = AccessGraph.NewWorkspace("Workspace B");
        var roleInB = AccessGraph.NewWorkspaceRole(workspaceB.Id);
        var permission = AccessGraph.NewPermission(PermissionCode);
        var membershipInA = AccessGraph.NewMembership(user.Id, workspaceA.Id);

        seeding.Users.Add(user);
        seeding.Workspaces.Add(workspaceA);
        seeding.Workspaces.Add(workspaceB);
        seeding.Roles.Add(roleInB);
        seeding.Permissions.Add(permission);
        seeding.RolePermissions.Add(new RolePermission(roleInB.Id, permission.Id));
        seeding.WorkspaceMemberships.Add(membershipInA);
        seeding.WorkspaceMembershipRoles.Add(new WorkspaceMembershipRole(membershipInA.Id, roleInB.Id));
        await seeding.SaveChangesAsync();

        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, PermissionCode, workspaceA.Id));
        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, PermissionCode, workspaceB.Id));
    }

    [Fact]
    public async Task APlatformRoleCarryingThePermissionCannotSatisfyAWorkspaceTarget()
    {
        await using var seeding = Host.CreateVerificationContext();

        var (user, _) = await AccessGraph.SeedPlatformGrantAsync(seeding, PermissionCode);

        var workspace = AccessGraph.NewWorkspace();
        seeding.Workspaces.Add(workspace);
        seeding.WorkspaceMemberships.Add(AccessGraph.NewMembership(user.Id, workspace.Id));
        await seeding.SaveChangesAsync();

        // The user is even a member of the workspace. The grant is still platform-scoped, so it does not apply.
        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, PermissionCode, workspace.Id));
    }

    [Theory]
    [InlineData(UserStatus.Pending)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Deactivated)]
    public async Task ANonActiveUserIsDeniedEvenWithACompleteGrant(UserStatus status)
    {
        await using var seeding = Host.CreateVerificationContext();

        var user = status switch
        {
            UserStatus.Pending => AccessGraph.PendingUser(),
            UserStatus.Suspended => AccessGraph.SuspendedUser(),
            UserStatus.Deactivated => AccessGraph.DeactivatedUser(),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unhandled status.")
        };

        var workspace = AccessGraph.NewWorkspace();
        var permission = AccessGraph.NewPermission(PermissionCode);

        seeding.Users.Add(user);
        seeding.Workspaces.Add(workspace);
        seeding.Permissions.Add(permission);
        await AccessGraph.GrantInWorkspaceAsync(seeding, user, workspace, permission);

        Assert.Equal(status, user.Status);
        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, PermissionCode, workspace.Id));
    }

    [Fact]
    public async Task APermissionCodeIsMatchedWithItsExactCasing()
    {
        await using var seeding = Host.CreateVerificationContext();
        var (user, workspace, _) = await AccessGraph.SeedWorkspaceGrantAsync(seeding, "Property.Read");

        Assert.Same(AuthorizationDecision.Allowed, await CheckAsync(user.Id, "Property.Read", workspace.Id));
        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, "property.read", workspace.Id));
    }

    [Fact]
    public async Task AnUnknownWorkspaceIsDenied()
    {
        await using var seeding = Host.CreateVerificationContext();
        var (user, _, _) = await AccessGraph.SeedWorkspaceGrantAsync(seeding, PermissionCode);

        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, PermissionCode, Guid.NewGuid()));
    }

    private async Task<AuthorizationDecision> CheckAsync(Guid userId, string permissionCode, Guid workspaceId)
    {
        using var scope = Host.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IPermissionChecker>()
            .CheckAsync(AuthorizationRequest.For(
                userId,
                permissionCode,
                AuthorizationTarget.Workspace(workspaceId)));
    }
}
