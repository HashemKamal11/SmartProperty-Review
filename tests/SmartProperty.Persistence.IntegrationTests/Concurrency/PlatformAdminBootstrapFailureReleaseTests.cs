using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Application.Identity.Bootstrap;
using SmartProperty.Domain.Identity;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Concurrency;

public sealed class PlatformAdminBootstrapFailureReleaseTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    private static readonly TimeSpan LockReleaseGuard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task MissingUserFailure_ReleasesLockForSubsequentBootstrap()
    {
        var user = AccessGraph.ActiveUser($"platform-admin-{Guid.NewGuid():n}@example.test");
        await SeedUserAsync(user);

        using (var failingScope = Host.CreateScope())
        {
            var failed = await CreateHandler(failingScope).Handle(
                new BootstrapPlatformAdminCommand($"missing-{Guid.NewGuid():n}@example.test"));

            Assert.True(failed.IsFailure);
        }

        using var succeedingScope = Host.CreateScope();
        var succeeded = await CreateHandler(succeedingScope)
            .Handle(new BootstrapPlatformAdminCommand(user.Email))
            .WaitAsync(LockReleaseGuard);

        Assert.True(succeeded.IsSuccess);
    }

    [Fact]
    public async Task SaveFailure_RollsBackAndReleasesLockForSubsequentBootstrap()
    {
        var user = AccessGraph.PendingUser($"platform-admin-{Guid.NewGuid():n}@example.test");
        await SeedUserAsync(user);

        using (var failingScope = Host.CreateScope())
        {
            var handler = CreateHandler(
                failingScope,
                new ThrowingUnitOfWork(new InvalidOperationException("Simulated bootstrap save failure.")));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => handler.Handle(new BootstrapPlatformAdminCommand(user.Email)));
        }

        await using (var rolledBack = Host.CreateVerificationContext())
        {
            Assert.Equal(
                UserStatus.Pending,
                (await rolledBack.Users.SingleAsync(row => row.Id == user.Id)).Status);
            Assert.Empty(await rolledBack.Permissions.ToListAsync());
            Assert.Empty(await rolledBack.Roles.ToListAsync());
            Assert.Empty(await rolledBack.RolePermissions.ToListAsync());
            Assert.Empty(await rolledBack.UserPlatformRoles.ToListAsync());
        }

        using var succeedingScope = Host.CreateScope();
        var succeeded = await CreateHandler(succeedingScope)
            .Handle(new BootstrapPlatformAdminCommand(user.Email))
            .WaitAsync(LockReleaseGuard);

        Assert.True(succeeded.IsSuccess);

        await using var verification = Host.CreateVerificationContext();
        Assert.Equal(UserStatus.Active, (await verification.Users.SingleAsync(row => row.Id == user.Id)).Status);
        Assert.Single(await verification.Roles.ToListAsync());
    }

    private BootstrapPlatformAdminCommandHandler CreateHandler(IServiceScope scope, IUnitOfWork? unitOfWork = null)
    {
        return new BootstrapPlatformAdminCommandHandler(
            scope.ServiceProvider.GetRequiredService<IUserRepository>(),
            scope.ServiceProvider.GetRequiredService<IPermissionRepository>(),
            scope.ServiceProvider.GetRequiredService<IRoleRepository>(),
            Host.Clock,
            unitOfWork ?? scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IPlatformAdminBootstrapCoordinator>());
    }

    private async Task SeedUserAsync(User user)
    {
        await using var context = Host.CreateVerificationContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();
    }

    private sealed class ThrowingUnitOfWork(Exception exception) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromException<int>(exception);
        }

        public void DiscardTrackedChanges()
        {
        }
    }
}
