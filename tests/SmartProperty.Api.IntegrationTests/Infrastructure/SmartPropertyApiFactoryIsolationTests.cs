using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Proves the test host does not inherit the developer's machine configuration, so the suite gives the same
/// answer on every machine whether or not the API project has a <c>UserSecretsId</c>.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class SmartPropertyApiFactoryIsolationTests(ApiPostgreSqlFixture fixture)
{
    [Fact]
    public void UserSecretsSourceIsRecognizedByItsTypeAndPath()
    {
        var builder = new ConfigurationBuilder().AddUserSecrets("isolation-test-secrets-id");

        Assert.Contains(builder.Sources, SmartPropertyApiFactory.IsUserSecretsSource);
    }

    [Fact]
    public void HostConfigurationHasNoUserSecretsSource()
    {
        using var factory = new SmartPropertyApiFactory(fixture);

        var root = Assert.IsAssignableFrom<IConfigurationRoot>(factory.Services.GetRequiredService<IConfiguration>());

        Assert.DoesNotContain(
            root.Providers.OfType<JsonConfigurationProvider>(),
            provider => SmartPropertyApiFactory.IsUserSecretsSource(provider.Source));
    }

    [Fact]
    public void BootstrapIsDisabledByDefault()
    {
        using var factory = new SmartPropertyApiFactory(fixture);

        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        Assert.False(configuration.GetValue<bool>("Bootstrap:Enabled"));
        Assert.Equal(string.Empty, configuration["Bootstrap:PlatformAdminEmail"]);
    }

    [Fact]
    public void ExplicitBootstrapConfigurationOverridesTheDisabledDefault()
    {
        using var factory = new SmartPropertyApiFactory(
            fixture,
            configuration:
            [
                new KeyValuePair<string, string?>("Bootstrap:Enabled", "true"),
                new KeyValuePair<string, string?>("Bootstrap:PlatformAdminEmail", $"missing-{Guid.NewGuid():N}@example.test")
            ]);

        // Starting the host runs the bootstrap, which fails for an account that does not exist. That failure is
        // the proof that the explicit setting won over the factory's disabled default.
        var exception = Assert.Throws<InvalidOperationException>(() => factory.Services);

        Assert.Contains("bootstrap.platform_admin_not_found", exception.Message, StringComparison.Ordinal);
    }
}
