using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Authorization;
using SmartProperty.Application.Authorization;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Authorization;

/// <summary>
/// Counts the database round trips one authorization question costs.
/// </summary>
/// <remarks>
/// Each path is a single existence query, so an answer must cost exactly one command whether it is an allow or
/// a denial. A number above one would mean the access graph is being walked row by row, which is both slower and
/// a different security story: partial results would exist where today there is only a yes or a no.
///
/// Seeding runs through a scope so it reaches the counter too, which is what makes the reset meaningful — the
/// count asserted afterwards can only come from the check itself. Timing is never measured, and the captured SQL
/// is recorded only so a failure can say what ran; no test asserts on its text.
/// </remarks>
public sealed class PermissionCheckerQueryCountTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private const string PermissionCode = "property.read";

    [Fact]
    public async Task AnAllowedPlatformCheckCostsOneDatabaseCommand()
    {
        Guid userId;

        using (var seeding = Host.CreateScope())
        {
            var context = seeding.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var (user, _) = await AccessGraph.SeedPlatformGrantAsync(context, PermissionCode);
            userId = user.Id;
        }

        Assert.NotEqual(0, Host.Commands.Count);
        Host.Commands.Reset();

        var decision = await CheckAsync(userId, PermissionCode, AuthorizationTarget.Platform);

        Assert.Same(AuthorizationDecision.Allowed, decision);
        Assert.Equal(1, Host.Commands.Count);
    }

    [Fact]
    public async Task ADeniedPlatformCheckCostsOneDatabaseCommand()
    {
        using (var seeding = Host.CreateScope())
        {
            var context = seeding.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await AccessGraph.SeedPlatformGrantAsync(context, PermissionCode);
        }

        Host.Commands.Reset();

        var decision = await CheckAsync(Guid.NewGuid(), PermissionCode, AuthorizationTarget.Platform);

        Assert.Same(AuthorizationDecision.Denied, decision);
        Assert.Equal(1, Host.Commands.Count);
    }

    [Fact]
    public async Task AnAllowedWorkspaceCheckCostsOneDatabaseCommand()
    {
        Guid userId;
        Guid workspaceId;

        using (var seeding = Host.CreateScope())
        {
            var context = seeding.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var (user, workspace, _) = await AccessGraph.SeedWorkspaceGrantAsync(context, PermissionCode);
            userId = user.Id;
            workspaceId = workspace.Id;
        }

        Assert.NotEqual(0, Host.Commands.Count);
        Host.Commands.Reset();

        var decision = await CheckAsync(userId, PermissionCode, AuthorizationTarget.Workspace(workspaceId));

        Assert.Same(AuthorizationDecision.Allowed, decision);
        Assert.Equal(1, Host.Commands.Count);
    }

    [Fact]
    public async Task ADeniedWorkspaceCheckCostsOneDatabaseCommand()
    {
        Guid userId;

        using (var seeding = Host.CreateScope())
        {
            var context = seeding.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var (user, _, _) = await AccessGraph.SeedWorkspaceGrantAsync(context, PermissionCode);
            userId = user.Id;
        }

        Host.Commands.Reset();

        var decision = await CheckAsync(userId, PermissionCode, AuthorizationTarget.Workspace(Guid.NewGuid()));

        Assert.Same(AuthorizationDecision.Denied, decision);
        Assert.Equal(1, Host.Commands.Count);
    }

    [Fact]
    public async Task TheCheckCostDoesNotGrowWithTheSizeOfTheAccessGraph()
    {
        // Guards the shape of the query rather than a constant: adding more workspaces, roles, and grants for
        // the same user must not add round trips.
        Guid userId;
        Guid workspaceId;

        using (var seeding = Host.CreateScope())
        {
            var context = seeding.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var (user, workspace, permission) = await AccessGraph.SeedWorkspaceGrantAsync(context, PermissionCode);
            userId = user.Id;
            workspaceId = workspace.Id;

            for (var index = 0; index < 5; index++)
            {
                var other = AccessGraph.NewWorkspace($"Workspace {index}");
                context.Workspaces.Add(other);
                await context.SaveChangesAsync();

                await AccessGraph.GrantInWorkspaceAsync(context, user, other, permission);
            }
        }

        Host.Commands.Reset();

        var decision = await CheckAsync(userId, PermissionCode, AuthorizationTarget.Workspace(workspaceId));

        Assert.Same(AuthorizationDecision.Allowed, decision);
        Assert.Equal(1, Host.Commands.Count);
    }

    private async Task<AuthorizationDecision> CheckAsync(
        Guid userId,
        string permissionCode,
        AuthorizationTarget target)
    {
        using var scope = Host.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IPermissionChecker>()
            .CheckAsync(AuthorizationRequest.For(userId, permissionCode, target));
    }
}
