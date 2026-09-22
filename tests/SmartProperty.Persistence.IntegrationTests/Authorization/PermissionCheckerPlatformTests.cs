using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Identity;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Authorization;

/// <summary>
/// Resolves platform permissions through the real <c>PermissionChecker</c> against rows that are actually in
/// PostgreSQL.
/// </summary>
/// <remarks>
/// Nothing is stubbed: the checker is resolved from the container, it queries the real
/// <see cref="ApplicationDbContext"/>, and every allow depends on a complete chain of persisted rows — user,
/// user_platform_roles, roles, role_permissions, permissions. Removing any one of them is what each denial test
/// does.
///
/// There is no implicit administrator and no special-cased role name anywhere in these tests, because there is
/// none in the checker: an allow can only come from a persisted role-permission relationship.
/// </remarks>
public sealed class PermissionCheckerPlatformTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string PermissionCode = "platform.workspace.create";

    [Fact]
    public async Task ACompletePlatformGrantIsAllowed()
    {
        await using var seeding = Host.CreateVerificationContext();
        var (user, _) = await AccessGraph.SeedPlatformGrantAsync(seeding, PermissionCode);

        Assert.Same(AuthorizationDecision.Allowed, await CheckAsync(user.Id, PermissionCode));
    }

    [Fact]
    public async Task AUserWithNoRoleAtAllIsDenied()
    {
        await using var seeding = Host.CreateVerificationContext();

        var user = AccessGraph.ActiveUser();
        seeding.Users.Add(user);
        seeding.Permissions.Add(AccessGraph.NewPermission(PermissionCode));
        await seeding.SaveChangesAsync();

        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, PermissionCode));
    }

    [Fact]
    public async Task AUserHoldingARoleThatDoesNotCarryThePermissionIsDenied()
    {
        await using var seeding = Host.CreateVerificationContext();

        var user = AccessGraph.ActiveUser();
        var role = AccessGraph.NewPlatformRole();

        seeding.Users.Add(user);
        seeding.Roles.Add(role);
        seeding.Permissions.Add(AccessGraph.NewPermission(PermissionCode));
        seeding.UserPlatformRoles.Add(new UserPlatformRole(user.Id, role.Id));
        await seeding.SaveChangesAsync();

        // The role exists and is assigned; only the role_permissions row is missing.
        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, PermissionCode));
    }

    [Fact]
    public async Task AGrantedRoleThatIsNotAssignedToTheUserIsDenied()
    {
        await using var seeding = Host.CreateVerificationContext();

        var user = AccessGraph.ActiveUser();
        var role = AccessGraph.NewPlatformRole();
        var permission = AccessGraph.NewPermission(PermissionCode);

        seeding.Users.Add(user);
        seeding.Roles.Add(role);
        seeding.Permissions.Add(permission);
        seeding.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
        await seeding.SaveChangesAsync();

        // Only the user_platform_roles row is missing.
        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, PermissionCode));
    }

    [Fact]
    public async Task AnUnknownUserIsDenied()
    {
        await using var seeding = Host.CreateVerificationContext();
        await AccessGraph.SeedPlatformGrantAsync(seeding, PermissionCode);

        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(Guid.NewGuid(), PermissionCode));
    }

    [Fact]
    public async Task AnUnknownPermissionCodeIsDenied()
    {
        await using var seeding = Host.CreateVerificationContext();
        var (user, _) = await AccessGraph.SeedPlatformGrantAsync(seeding, PermissionCode);

        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, "platform.workspace.delete"));
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

        var role = AccessGraph.NewPlatformRole();
        var permission = AccessGraph.NewPermission(PermissionCode);

        seeding.Users.Add(user);
        seeding.Roles.Add(role);
        seeding.Permissions.Add(permission);
        seeding.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
        seeding.UserPlatformRoles.Add(new UserPlatformRole(user.Id, role.Id));
        await seeding.SaveChangesAsync();

        Assert.Equal(status, user.Status);
        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, PermissionCode));
    }

    [Fact]
    public async Task AWorkspaceRoleCarryingThePermissionCannotSatisfyAPlatformTarget()
    {
        await using var seeding = Host.CreateVerificationContext();
        var (user, _, _) = await AccessGraph.SeedWorkspaceGrantAsync(seeding, PermissionCode);

        // Everything the workspace path needs is persisted. The platform path must still find nothing.
        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, PermissionCode));
    }

    [Fact]
    public async Task APermissionCodeIsMatchedWithItsExactCasing()
    {
        await using var seeding = Host.CreateVerificationContext();
        var (user, _) = await AccessGraph.SeedPlatformGrantAsync(seeding, "Property.Read");

        Assert.Same(AuthorizationDecision.Allowed, await CheckAsync(user.Id, "Property.Read"));

        // PostgreSQL compares the stored code exactly, so a lower-cased code is a different permission.
        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, "property.read"));
        Assert.Same(AuthorizationDecision.Denied, await CheckAsync(user.Id, "PROPERTY.READ"));
    }

    private async Task<AuthorizationDecision> CheckAsync(Guid userId, string permissionCode)
    {
        using var scope = Host.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IPermissionChecker>()
            .CheckAsync(AuthorizationRequest.For(userId, permissionCode, AuthorizationTarget.Platform));
    }
}
