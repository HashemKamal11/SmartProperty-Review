using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SmartProperty.Api.Infrastructure.Development;
using SmartProperty.Application.Abstractions.Marketplace;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Development;

/// <summary>
/// Demo mode needs two things at once: the Development environment and the explicit flag. Outside Development the flag
/// makes the host refuse to start, and without the flag nothing simulated exists, not even the fixture endpoint.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class DemoModeTests(ApiPostgreSqlFixture fixture)
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "smartproperty-demo-" + Guid.NewGuid().ToString("N"));

    // ---- the options themselves

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    [InlineData("Testing")]
    [InlineData("")]
    public void TheFlagIsRefusedInEveryEnvironmentExceptDevelopment(string environment)
    {
        var result = new DemoModeOptionsValidator(new Environment(environment))
            .Validate(null, new DemoModeOptions { Enabled = true, EvidenceRootPath = Root });

        Assert.True(result.Failed);
        Assert.Contains("refuses to start", string.Join(' ', result.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentWithTheFlagNeedsAnEvidenceFolderAndASaneSizeLimit()
    {
        var validator = new DemoModeOptionsValidator(new Environment("Development"));

        Assert.True(validator.Validate(null, new DemoModeOptions { Enabled = true }).Failed);
        Assert.True(validator.Validate(null, new DemoModeOptions { Enabled = true, EvidenceRootPath = Root, MaxEvidenceBytes = 0 }).Failed);
        Assert.True(validator.Validate(null, new DemoModeOptions { Enabled = true, EvidenceRootPath = Root, MaxEvidenceBytes = 51L * 1024 * 1024 }).Failed);
        Assert.True(validator.Validate(null, new DemoModeOptions { Enabled = true, EvidenceRootPath = Root }).Succeeded);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void WithoutTheFlagNothingIsRequiredAnywhere(string environment)
    {
        Assert.True(new DemoModeOptionsValidator(new Environment(environment)).Validate(null, new DemoModeOptions()).Succeeded);
    }

    // ---- the running host

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void AHostWithTheFlagOutsideDevelopmentRefusesToStart(string environment)
    {
        using var factory = Factory(environment, enabled: true);

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("DemoMode", Flatten(exception), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InDevelopmentWithTheFlagTheSimulatedProvidersAndTheFixtureEndpointExist()
    {
        using var factory = Factory("Development", enabled: true);
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();

        Assert.IsType<SimulatedEvidenceAssessmentProvider>(scope.ServiceProvider.GetRequiredService<IEvidenceAssessmentProvider>());
        Assert.IsType<RelationshipReviewerIndependenceProvider>(scope.ServiceProvider.GetRequiredService<IReviewerIndependenceProvider>());

        // The endpoint exists (and wants a signed-in user), and it is in the OpenAPI document.
        using var anonymous = await client.PostAsync("/api/development/evidence", new MultipartFormDataContent());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains("/api/development/evidence", await client.GetStringAsync("/openapi/v1.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InDevelopmentWithoutTheFlagNothingSimulatedExistsNotEvenTheEndpoint()
    {
        using var factory = Factory("Development", enabled: false);
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();

        Assert.Equal("UnavailableEvidenceAssessmentProvider", scope.ServiceProvider.GetRequiredService<IEvidenceAssessmentProvider>().GetType().Name);
        Assert.Equal("UnavailableReviewerIndependenceProvider", scope.ServiceProvider.GetRequiredService<IReviewerIndependenceProvider>().GetType().Name);

        using var response = await client.PostAsync("/api/development/evidence", new MultipartFormDataContent());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("/api/development/evidence", await client.GetStringAsync("/openapi/v1.json"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void StagingAndProductionWithoutTheFlagKeepTheFailClosedProviders(string environment)
    {
        using var factory = Factory(environment, enabled: false);
        using var scope = factory.Services.CreateScope();

        Assert.Equal("UnavailableEvidenceAssessmentProvider", scope.ServiceProvider.GetRequiredService<IEvidenceAssessmentProvider>().GetType().Name);
        Assert.Equal("UnavailableReviewerIndependenceProvider", scope.ServiceProvider.GetRequiredService<IReviewerIndependenceProvider>().GetType().Name);
    }

    // ---- helpers

    private SmartPropertyApiFactory Factory(string environment, bool enabled) => new(
        fixture,
        usePersistencePermissionChecker: true,
        configuration:
        [
            new KeyValuePair<string, string?>("DemoMode:Enabled", enabled ? "true" : "false"),
            new KeyValuePair<string, string?>("DemoMode:EvidenceRootPath", Root),
        ],
        environment: environment);

    private static string Flatten(Exception exception)
    {
        var text = new System.Text.StringBuilder();

        for (var current = exception; current is not null; current = current.InnerException!)
        {
            text.AppendLine(current.Message);

            if (current.InnerException is null)
            {
                break;
            }
        }

        return text.ToString();
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
