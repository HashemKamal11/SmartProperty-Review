using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Migrator;
using SmartProperty.Persistence;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Deployment;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class MigrationProvisioningTests(ApiPostgreSqlFixture fixture)
{
    [Fact]
    public async Task BlankDatabase_AppliesAllMigrationsAndCreatesConfiguredWorkspace()
    {
        var connectionString = await fixture.CreateBlankDatabaseAsync();
        var workspaceId = Guid.NewGuid();

        await RunMigratorAsync(connectionString, enabled: true, workspaceId.ToString(), "  First Workspace  ");

        await using var context = ApiPostgreSqlFixture.CreateContext(connectionString);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());

        var workspace = await context.Workspaces.SingleAsync();
        Assert.Equal(workspaceId, workspace.Id);
        Assert.Equal("First Workspace", workspace.Name);
    }

    [Fact]
    public async Task SecondRun_WithMatchingIdAndNormalizedName_IsANoOp()
    {
        var connectionString = await fixture.CreateBlankDatabaseAsync();
        var workspaceId = Guid.NewGuid();

        await RunMigratorAsync(connectionString, enabled: true, workspaceId.ToString(), "Existing Workspace");
        await RunMigratorAsync(connectionString, enabled: true, workspaceId.ToString(), " Existing Workspace ");

        await using var context = ApiPostgreSqlFixture.CreateContext(connectionString);
        var workspace = await context.Workspaces.SingleAsync();

        Assert.Equal(workspaceId, workspace.Id);
        Assert.Equal("Existing Workspace", workspace.Name);
    }

    [Fact]
    public async Task ProvisioningDisabled_AppliesSchemaWithoutCreatingAWorkspace()
    {
        var connectionString = await fixture.CreateBlankDatabaseAsync();

        await RunMigratorAsync(connectionString, enabled: false, string.Empty, string.Empty);

        await using var context = ApiPostgreSqlFixture.CreateContext(connectionString);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(await context.Workspaces.AnyAsync());
    }

    [Theory]
    [InlineData("not-a-guid", "Workspace")]
    [InlineData("00000000-0000-0000-0000-000000000000", "Workspace")]
    [InlineData("11111111-1111-4111-8111-111111111111", "   ")]
    public async Task InvalidEnabledConfiguration_FailsWithoutCreatingAWorkspace(string id, string name)
    {
        var connectionString = await fixture.CreateBlankDatabaseAsync();
        await RunMigratorAsync(connectionString, enabled: false, string.Empty, string.Empty);

        await Assert.ThrowsAsync<MigrationProvisioningException>(
            () => RunMigratorAsync(connectionString, enabled: true, id, name));

        await using var context = ApiPostgreSqlFixture.CreateContext(connectionString);
        Assert.False(await context.Workspaces.AnyAsync());
    }

    [Fact]
    public async Task ExistingConfiguredIdWithDifferentName_FailsAndLeavesWorkspaceUnchanged()
    {
        var connectionString = await fixture.CreateBlankDatabaseAsync();
        var workspaceId = Guid.NewGuid();

        await RunMigratorAsync(connectionString, enabled: true, workspaceId.ToString(), "Original Name");

        var failure = await Assert.ThrowsAsync<MigrationProvisioningException>(
            () => RunMigratorAsync(connectionString, enabled: true, workspaceId.ToString(), "Different Name"));

        Assert.Contains("conflict", failure.Message, StringComparison.OrdinalIgnoreCase);

        await using var context = ApiPostgreSqlFixture.CreateContext(connectionString);
        var workspace = await context.Workspaces.SingleAsync();
        Assert.Equal(workspaceId, workspace.Id);
        Assert.Equal("Original Name", workspace.Name);
    }

    internal static async Task RunMigratorAsync(
        string connectionString,
        bool enabled,
        string id,
        string name)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString,
                ["Provisioning:InitialWorkspace:Enabled"] = enabled.ToString(),
                ["Provisioning:InitialWorkspace:Id"] = id,
                ["Provisioning:InitialWorkspace:Name"] = name
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPersistence(configuration);
        services.AddOptions<InitialWorkspaceOptions>()
            .Bind(configuration.GetSection(InitialWorkspaceOptions.SectionName));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<MigrationProvisioningRunner>();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<MigrationProvisioningRunner>()
            .RunAsync();
    }
}
