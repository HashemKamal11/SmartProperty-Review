using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Authorization;
using SmartProperty.Application.Identity.Bootstrap;
using SmartProperty.Common.Results;
using SmartProperty.Domain.Identity;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Concurrency;

/// <summary>
/// Proves that independent bootstrap scopes coordinate through PostgreSQL before any read-before-create work.
/// </summary>
public sealed class PlatformAdminBootstrapConcurrencyTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private readonly PlatformAdminBootstrapLockBarrier lockBarrier = new();

    private protected override IInterceptor[] ExtraInterceptors()
    {
        return [lockBarrier];
    }

    [Fact]
    public async Task TwoConcurrentEmptyBootstraps_CreateOneCompletePlatformAdminGrant()
    {
        var user = AccessGraph.PendingUser($"platform-admin-{Guid.NewGuid():n}@example.test");
        await SeedAsync(user);

        var (first, second) = await RunOverlappingBootstrapsAsync(user.Email);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.False(first.Value.MadeNoChange);
        Assert.True(second.Value.MadeNoChange);
        Assert.True(first.Value.UserActivated);
        Assert.False(second.Value.UserActivated);

        await AssertSingleCompleteGrantAsync(user.Id, expectedUserStatus: UserStatus.Active);
    }

    [Fact]
    public async Task TwoConcurrentBootstraps_WhenPermissionExists_CreateOnePlatformAdminRoleAndGrant()
    {
        var user = AccessGraph.ActiveUser($"platform-admin-{Guid.NewGuid():n}@example.test");
        var permission = new Permission(
            Guid.NewGuid(),
            PermissionCodes.WorkspaceAccessRequestsReview,
            PermissionCodes.WorkspaceAccessRequestsReviewDescription);
        await SeedAsync(user, permission);

        var (first, second) = await RunOverlappingBootstrapsAsync(user.Email);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.False(first.Value.MadeNoChange);
        Assert.True(second.Value.MadeNoChange);
        Assert.False(first.Value.PermissionCreated);
        Assert.False(second.Value.PermissionCreated);
        Assert.True(first.Value.RoleCreated);
        Assert.False(second.Value.RoleCreated);

        await AssertSingleCompleteGrantAsync(user.Id, expectedUserStatus: UserStatus.Active);
    }

    private async Task<(Result<BootstrapPlatformAdminResult> First, Result<BootstrapPlatformAdminResult> Second)>
        RunOverlappingBootstrapsAsync(string email)
    {
        using var firstScope = Host.CreateScope();
        using var secondScope = Host.CreateScope();
        var firstHandler = CreateHandler(firstScope);
        var secondHandler = CreateHandler(secondScope);
        var command = new BootstrapPlatformAdminCommand(email);

        var firstTask = firstHandler.Handle(command);
        await lockBarrier.WaitUntilFirstLockIsAcquiredAsync();

        var secondTask = secondHandler.Handle(command);

        try
        {
            await lockBarrier.WaitUntilSecondLockIsAttemptedAsync();

            // The second command has reached PostgreSQL but cannot finish acquiring the transaction lock while
            // the first is deliberately paused. Releasing A lets it read, save, and commit before B can read.
            Assert.False(secondTask.IsCompleted);
        }
        finally
        {
            lockBarrier.ReleaseFirst();
        }

        return (await firstTask, await secondTask);
    }

    private BootstrapPlatformAdminCommandHandler CreateHandler(IServiceScope scope)
    {
        return new BootstrapPlatformAdminCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserRepository>(),
            scope.ServiceProvider.GetRequiredService<IPermissionRepository>(),
            scope.ServiceProvider.GetRequiredService<IRoleRepository>(),
            Host.Clock,
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IPlatformAdminBootstrapCoordinator>());
    }

    private async Task SeedAsync(User user, Permission? permission = null)
    {
        await using var context = Host.CreateVerificationContext();
        context.Users.Add(user);

        if (permission is not null)
        {
            context.Permissions.Add(permission);
        }

        await context.SaveChangesAsync();
    }

    private async Task AssertSingleCompleteGrantAsync(Guid userId, UserStatus expectedUserStatus)
    {
        await using var context = Host.CreateVerificationContext();

        var user = await context.Users.SingleAsync(row => row.Id == userId);
        var permission = await context.Permissions.SingleAsync(
            row => row.Code == PermissionCodes.WorkspaceAccessRequestsReview);
        var role = await context.Roles.SingleAsync(row =>
            row.Name == BootstrapPlatformAdminCommandHandler.PlatformAdminRoleName
            && row.Scope == RoleScope.Platform
            && row.WorkspaceId == null);

        Assert.Equal(expectedUserStatus, user.Status);
        Assert.Equal(1, await context.Permissions.CountAsync(
            row => row.Code == PermissionCodes.WorkspaceAccessRequestsReview));
        Assert.Equal(1, await context.Roles.CountAsync(row =>
            row.Name == BootstrapPlatformAdminCommandHandler.PlatformAdminRoleName
            && row.Scope == RoleScope.Platform
            && row.WorkspaceId == null));
        Assert.Equal(1, await context.RolePermissions.CountAsync(row =>
            row.RoleId == role.Id && row.PermissionId == permission.Id));
        Assert.Equal(1, await context.UserPlatformRoles.CountAsync(row =>
            row.UserId == userId && row.RoleId == role.Id));
    }
}
