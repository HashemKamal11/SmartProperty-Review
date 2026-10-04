using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Application.Abstractions.Persistence.Repositories;
using SmartProperty.Domain.Workspaces;
using SmartProperty.Persistence.Context;
using SmartProperty.Persistence.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Persistence.IntegrationTests.Persistence;

/// <summary>
/// The registration options query against real PostgreSQL: what it returns, in which order, and that it is a
/// projection rather than a load of tracked entities.
/// </summary>
/// <remarks>
/// Each test owns a private database, so "every workspace" really means the workspaces the test seeded.
/// </remarks>
public sealed class WorkspaceRegistrationOptionsRepositoryTests(PostgreSqlFixture fixture) : DatabaseTest(fixture)
{
    [Fact]
    public async Task NoWorkspaces_ReturnsAnEmptyList()
    {
        using var scope = Host.CreateScope();
        var workspaces = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();

        var options = await workspaces.ListRegistrationOptionsAsync();

        Assert.Empty(options);
    }

    [Fact]
    public async Task EveryWorkspaceIsReturnedOrderedByNameThenId()
    {
        // The two equally named workspaces differ only in the first byte of the id, so PostgreSQL's uuid order and
        // the order asserted here cannot disagree.
        var charlie = new Workspace(Guid.NewGuid(), "Charlie", TestClock.DefaultNow);
        var alpha = new Workspace(Guid.NewGuid(), "Alpha", TestClock.DefaultNow);
        var sameNameHigherId = new Workspace(
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000"),
            "Bravo",
            TestClock.DefaultNow);
        var sameNameLowerId = new Workspace(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000"),
            "Bravo",
            TestClock.DefaultNow);

        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Workspaces.AddRange(charlie, alpha, sameNameHigherId, sameNameLowerId);
            await seeding.SaveChangesAsync();
        }

        using var scope = Host.CreateScope();
        var workspaces = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();

        var options = await workspaces.ListRegistrationOptionsAsync();

        Assert.Equal(
            [alpha.Id, sameNameLowerId.Id, sameNameHigherId.Id, charlie.Id],
            options.Select(option => option.Id));
        Assert.Equal(
            ["Alpha", "Bravo", "Bravo", "Charlie"],
            options.Select(option => option.Name));
    }

    [Fact]
    public async Task TheOptionsAreProjectedAndNothingIsTracked()
    {
        await using (var seeding = Host.CreateVerificationContext())
        {
            seeding.Workspaces.Add(AccessGraph.NewWorkspace("Tracked Probe"));
            await seeding.SaveChangesAsync();
        }

        using var scope = Host.CreateScope();
        var workspaces = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();

        var option = Assert.Single(await workspaces.ListRegistrationOptionsAsync());

        Assert.Equal("Tracked Probe", option.Name);
        Assert.Empty(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ChangeTracker.Entries());
    }
}
