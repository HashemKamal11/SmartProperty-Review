using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Authorization;

public sealed class AccessContextReaderTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task ActiveUserWithNoAssignmentsReturnsEmptyContextInTwoQueries()
    {
        var user = AccessGraph.ActiveUser();
        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Users.Add(user);
            await seeding.SaveChangesAsync();
        }

        using var scope = Host.CreateScope();
        Host.Commands.Reset();

        var context = await scope.ServiceProvider.GetRequiredService<IAccessContextReader>().ReadAsync(user.Id);

        Assert.Empty(context.PlatformRoles);
        Assert.Empty(context.PlatformPermissions);
        Assert.Empty(context.Workspaces);
        Assert.Equal(2, Host.Commands.Count);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ChangeTracker.Entries());
    }

    [Fact]
    public async Task PlatformContextIsDistinctOrdinalAndMatchesPermissionChecker()
    {
        var user = AccessGraph.ActiveUser();
        var alpha = AccessGraph.NewPlatformRole("Alpha Platform");
        var auditor = AccessGraph.NewPlatformRole("Auditor");
        var zulu = AccessGraph.NewPlatformRole("Zulu Platform");
        var shared = AccessGraph.NewPermission("shared.permission");

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Users.Add(user);
            seeding.Roles.AddRange(zulu, auditor, alpha);
            seeding.Permissions.Add(shared);
            seeding.UserPlatformRoles.AddRange(
                new UserPlatformRole(user.Id, zulu.Id),
                new UserPlatformRole(user.Id, auditor.Id),
                new UserPlatformRole(user.Id, alpha.Id));
            seeding.RolePermissions.AddRange(
                new RolePermission(zulu.Id, shared.Id),
                new RolePermission(alpha.Id, shared.Id));
            await seeding.SaveChangesAsync();
        }

        using var scope = Host.CreateScope();
        var context = await scope.ServiceProvider.GetRequiredService<IAccessContextReader>().ReadAsync(user.Id);

        Assert.Equal(["Alpha Platform", "Auditor", "Zulu Platform"], context.PlatformRoles);
        Assert.Equal(["shared.permission"], context.PlatformPermissions);

        var checker = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();
        Assert.Same(
            AuthorizationDecision.Allowed,
            await checker.CheckAsync(AuthorizationRequest.For(
                user.Id,
                Assert.Single(context.PlatformPermissions),
                AuthorizationTarget.Platform)));
    }

    [Fact]
    public async Task WorkspaceContextsPreserveEmptyMembershipsAndExcludeCrossWorkspaceRoles()
    {
        var user = AccessGraph.ActiveUser();
        var alpha = AccessGraph.NewWorkspace("Alpha");
        var beta = AccessGraph.NewWorkspace("Beta");
        var zulu = AccessGraph.NewWorkspace("Zulu");
        var crossWorkspace = AccessGraph.NewWorkspace("Cross Workspace");

        var alphaMembership = AccessGraph.NewMembership(user.Id, alpha.Id);
        var betaMembership = AccessGraph.NewMembership(user.Id, beta.Id);
        var zuluMembership = AccessGraph.NewMembership(user.Id, zulu.Id);

        var editor = AccessGraph.NewWorkspaceRole(alpha.Id, "Editor");
        var viewer = AccessGraph.NewWorkspaceRole(alpha.Id, "Viewer");
        var betaRoleWithoutPermission = AccessGraph.NewWorkspaceRole(beta.Id, "Member");
        var crossRole = AccessGraph.NewWorkspaceRole(crossWorkspace.Id, "Foreign Role");
        var platformRole = AccessGraph.NewPlatformRole("Platform Only");

        var shared = AccessGraph.NewPermission("shared.permission");
        var alphaOnly = AccessGraph.NewPermission("workspace.alpha");
        var poison = AccessGraph.NewPermission("cross.workspace.poison");

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Users.Add(user);
            seeding.Workspaces.AddRange(zulu, beta, alpha, crossWorkspace);
            seeding.WorkspaceMemberships.AddRange(zuluMembership, betaMembership, alphaMembership);
            seeding.Roles.AddRange(editor, viewer, betaRoleWithoutPermission, crossRole, platformRole);
            seeding.Permissions.AddRange(shared, alphaOnly, poison);

            seeding.WorkspaceMembershipRoles.AddRange(
                new WorkspaceMembershipRole(alphaMembership.Id, viewer.Id),
                new WorkspaceMembershipRole(alphaMembership.Id, editor.Id),
                new WorkspaceMembershipRole(alphaMembership.Id, crossRole.Id),
                new WorkspaceMembershipRole(betaMembership.Id, betaRoleWithoutPermission.Id));

            seeding.RolePermissions.AddRange(
                new RolePermission(viewer.Id, shared.Id),
                new RolePermission(editor.Id, shared.Id),
                new RolePermission(editor.Id, alphaOnly.Id),
                new RolePermission(crossRole.Id, poison.Id),
                new RolePermission(platformRole.Id, shared.Id));
            seeding.UserPlatformRoles.Add(new UserPlatformRole(user.Id, platformRole.Id));
            await seeding.SaveChangesAsync();
        }

        using var scope = Host.CreateScope();
        Host.Commands.Reset();
        var context = await scope.ServiceProvider.GetRequiredService<IAccessContextReader>().ReadAsync(user.Id);

        Assert.Equal(["Alpha", "Beta", "Zulu"], context.Workspaces.Select(workspace => workspace.Name));

        var alphaContext = context.Workspaces[0];
        Assert.Equal(["Editor", "Viewer"], alphaContext.Roles);
        Assert.Equal(["shared.permission", "workspace.alpha"], alphaContext.Permissions);

        var betaContext = context.Workspaces[1];
        Assert.Equal(["Member"], betaContext.Roles);
        Assert.Empty(betaContext.Permissions);

        var zuluContext = context.Workspaces[2];
        Assert.Empty(zuluContext.Roles);
        Assert.Empty(zuluContext.Permissions);

        Assert.Equal(["shared.permission"], context.PlatformPermissions);
        Assert.Equal(2, Host.Commands.Count);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ChangeTracker.Entries());

        var checker = scope.ServiceProvider.GetRequiredService<IPermissionChecker>();

        foreach (var permissionCode in context.PlatformPermissions)
        {
            Assert.Same(
                AuthorizationDecision.Allowed,
                await checker.CheckAsync(AuthorizationRequest.For(
                    user.Id,
                    permissionCode,
                    AuthorizationTarget.Platform)));
        }

        foreach (var workspace in context.Workspaces)
        {
            foreach (var permissionCode in workspace.Permissions)
            {
                Assert.Same(
                    AuthorizationDecision.Allowed,
                    await checker.CheckAsync(AuthorizationRequest.For(
                        user.Id,
                        permissionCode,
                        AuthorizationTarget.Workspace(workspace.Id))));
            }
        }

        Assert.Same(
            AuthorizationDecision.Denied,
            await checker.CheckAsync(AuthorizationRequest.For(
                user.Id,
                poison.Code,
                AuthorizationTarget.Workspace(alpha.Id))));
        Assert.Same(
            AuthorizationDecision.Denied,
            await checker.CheckAsync(AuthorizationRequest.For(
                user.Id,
                alphaOnly.Code,
                AuthorizationTarget.Platform)));
        Assert.Same(
            AuthorizationDecision.Denied,
            await checker.CheckAsync(AuthorizationRequest.For(
                user.Id,
                shared.Code,
                AuthorizationTarget.Workspace(beta.Id))));
    }
}
