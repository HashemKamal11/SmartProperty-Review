using System.Net;
using System.Net.Http.Json;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class PropertyReadPermissionTests(ApiPostgreSqlFixture fixture)
{
    private readonly AuthScenario scenario = new(fixture);
    private static readonly KeyValuePair<string, string?>[] BootstrapDisabled =
    [
        new("Bootstrap:Enabled", "false")
    ];

    [Fact]
    public async Task PlatformPropertyReadAuthorizesListAndGetById()
    {
        var property = await SeedPropertyAsync();
        using var factory = new SmartPropertyApiFactory(
            fixture,
            usePersistencePermissionChecker: true,
            configuration: BootstrapDisabled);
        using var setup = factory.CreateClient();
        var user = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(user.UserId, PermissionCodes.PropertyRead);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var list = await client.GetAsync("/api/properties");
        using var getById = await client.GetAsync($"/api/properties/{property.Id}");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, getById.StatusCode);
    }

    [Fact]
    public async Task WorkspacePropertyReadDoesNotAuthorizeEitherPlatformReadEndpoint()
    {
        var property = await SeedPropertyAsync();
        using var factory = new SmartPropertyApiFactory(
            fixture,
            usePersistencePermissionChecker: true,
            configuration: BootstrapDisabled);
        using var setup = factory.CreateClient();
        var user = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantWorkspacePermissionAsync(
            user.UserId,
            user.WorkspaceId,
            PermissionCodes.PropertyRead);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var list = await client.GetAsync("/api/properties");
        using var getById = await client.GetAsync($"/api/properties/{property.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, getById.StatusCode);
    }

    [Fact]
    public async Task PropertyCreateAloneDoesNotAuthorizeEitherReadEndpoint()
    {
        var property = await SeedPropertyAsync();
        using var factory = new SmartPropertyApiFactory(
            fixture,
            usePersistencePermissionChecker: true,
            configuration: BootstrapDisabled);
        using var setup = factory.CreateClient();
        var user = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(user.UserId, PermissionCodes.PropertyCreate);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var list = await client.GetAsync("/api/properties");
        using var getById = await client.GetAsync($"/api/properties/{property.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, getById.StatusCode);
    }

    [Fact]
    public async Task PropertyReadAloneDoesNotAuthorizeCreate()
    {
        using var factory = new SmartPropertyApiFactory(
            fixture,
            usePersistencePermissionChecker: true,
            configuration: BootstrapDisabled);
        using var setup = factory.CreateClient();
        var user = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(user.UserId, PermissionCodes.PropertyRead);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var response = await client.PostAsJsonAsync(
            "/api/properties",
            new CreatePropertyRequest("Land", "SA"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<Property> SeedPropertyAsync()
    {
        var property = new Property(
            Guid.NewGuid(),
            PropertyType.Land,
            new PropertyAddress("SA"),
            DateTimeOffset.UtcNow);

        await using var context = fixture.CreateContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property;
    }
}
