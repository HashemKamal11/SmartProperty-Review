using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

public sealed class AdminWorkspaceRepositoryTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task PostgreSqlPagesDuplicatesDeterministicallyWithoutTracking()
    {
        var first = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
        var second = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");
        await using (var context = Host.CreateVerificationContext())
        {
            context.Workspaces.AddRange(new Workspace(second, "Same", TestClock.DefaultNow),
                new Workspace(first, "Same", TestClock.DefaultNow),
                new Workspace(Guid.NewGuid(), "Z", TestClock.DefaultNow));
            await context.SaveChangesAsync();
        }
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var page1 = await repository.ListAdministrativeAsync(0, 1);
        var page2 = await repository.ListAdministrativeAsync(1, 1);
        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(first, Assert.Single(page1.Items).Id);
        Assert.Equal(second, Assert.Single(page2.Items).Id);
        var beyond = await repository.ListAdministrativeAsync(10, 1);
        Assert.Empty(beyond.Items);
        Assert.Equal(3, beyond.TotalCount);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ChangeTracker.Entries());
    }

    [Fact]
    public async Task EmptyDirectoryAndInvalidBounds()
    {
        using var scope = Host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var result = await repository.ListAdministrativeAsync(0, 10);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.ListAdministrativeAsync(-1, 10));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.ListAdministrativeAsync(0, 101));
    }
}