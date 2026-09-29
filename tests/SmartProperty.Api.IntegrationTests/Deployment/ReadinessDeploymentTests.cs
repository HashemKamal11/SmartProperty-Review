using System.Net;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Deployment;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class ReadinessDeploymentTests(ApiPostgreSqlFixture fixture)
{
    private const string PreviousMigration = "20260927140557_SynchronizeWorkspaceAccessRequestStatusConcurrency";

    [Fact]
    public async Task ReadinessTracksBlankPendingAndCurrentSchemaWhileLivenessStaysHealthy()
    {
        var connectionString = await fixture.CreateBlankDatabaseAsync();

        using var factory = new SmartPropertyApiFactory(connectionString);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);

        await using (var context = ApiPostgreSqlFixture.CreateContext(connectionString))
        {
            await context.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);

        await MigrationProvisioningTests.RunMigratorAsync(
            connectionString,
            enabled: false,
            id: string.Empty,
            name: string.Empty);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task UnavailableDatabaseFailsReadinessButNotLiveness()
    {
        using var factory = new SmartPropertyApiFactory(fixture.BuildMissingDatabaseConnectionString());
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }
}
