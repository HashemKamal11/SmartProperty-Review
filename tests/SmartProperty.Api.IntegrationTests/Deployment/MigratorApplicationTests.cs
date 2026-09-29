using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Migrator;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Deployment;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class MigratorApplicationTests(ApiPostgreSqlFixture fixture)
{
    [Fact]
    public async Task ValidConfiguration_UsesProductionCompositionAndReturnsSuccess()
    {
        var connectionString = await fixture.CreateBlankDatabaseAsync();
        var workspaceId = Guid.NewGuid();

        var exitCode = await RunApplicationAsync(
            connectionString,
            enabled: true,
            workspaceId.ToString(),
            "Composition Workspace");

        Assert.Equal(0, exitCode);

        await using var context = ApiPostgreSqlFixture.CreateContext(connectionString);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());

        var workspace = await context.Workspaces.SingleAsync();
        Assert.Equal(workspaceId, workspace.Id);
        Assert.Equal("Composition Workspace", workspace.Name);
    }

    [Fact]
    public async Task InvalidEnabledConfiguration_UsesProductionFailureBoundaryAndReturnsOne()
    {
        var connectionString = await fixture.CreateBlankDatabaseAsync();

        var exitCode = await RunApplicationAsync(
            connectionString,
            enabled: true,
            Guid.Empty.ToString(),
            "Invalid Workspace");

        Assert.Equal(1, exitCode);

        await using var context = ApiPostgreSqlFixture.CreateContext(connectionString);
        Assert.Empty(await context.Database.GetAppliedMigrationsAsync());
    }

    private static Task<int> RunApplicationAsync(
        string connectionString,
        bool enabled,
        string id,
        string name)
    {
        return MigratorApplication.RunAsync(
            [],
            configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString,
                ["Provisioning:InitialWorkspace:Enabled"] = enabled.ToString(),
                ["Provisioning:InitialWorkspace:Id"] = id,
                ["Provisioning:InitialWorkspace:Name"] = name
            }),
            TextWriter.Null);
    }
}
