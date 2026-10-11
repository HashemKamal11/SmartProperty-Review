using System.Net;
using System.Net.Http.Json;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

/// <summary>
/// Update authorization through the production permission checker: only a platform-held <c>property.update</c>
/// satisfies it, on its own, and no other property permission or workspace grant does.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class UpdatePropertyPermissionTests(ApiPostgreSqlFixture fixture)
{
    private readonly AuthScenario scenario = new(fixture);
    private static readonly KeyValuePair<string, string?>[] BootstrapDisabled =
    [
        new("Bootstrap:Enabled", "false")
    ];

    [Fact]
    public async Task PlatformPropertyUpdateAloneAuthorizesUpdate()
    {
        var property = await SeedPropertyAsync();
        using var factory = CreateFactory();
        using var setup = factory.CreateClient();
        var user = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(user.UserId, PermissionCodes.PropertyUpdate);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var response = await client.PutAsJsonAsync(
            $"/api/properties/{property.Id}",
            new UpdatePropertyRequest("Unit", "SA"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WorkspacePropertyUpdateDoesNotAuthorizeUpdate()
    {
        var property = await SeedPropertyAsync();
        using var factory = CreateFactory();
        using var setup = factory.CreateClient();
        var user = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantWorkspacePermissionAsync(user.UserId, user.WorkspaceId, PermissionCodes.PropertyUpdate);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var response = await client.PutAsJsonAsync(
            $"/api/properties/{property.Id}",
            new UpdatePropertyRequest("Unit", "SA"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(PermissionCodes.PropertyRead)]
    [InlineData(PermissionCodes.PropertyCreate)]
    public async Task OtherPropertyPermissionsDoNotAuthorizeUpdate(string permissionCode)
    {
        var property = await SeedPropertyAsync();
        using var factory = CreateFactory();
        using var setup = factory.CreateClient();
        var user = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(user.UserId, permissionCode);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var response = await client.PutAsJsonAsync(
            $"/api/properties/{property.Id}",
            new UpdatePropertyRequest("Unit", "SA"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PropertyUpdateAloneDoesNotAuthorizeCreateOrRead()
    {
        var property = await SeedPropertyAsync();
        using var factory = CreateFactory();
        using var setup = factory.CreateClient();
        var user = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(user.UserId, PermissionCodes.PropertyUpdate);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var create = await client.PostAsJsonAsync("/api/properties", new CreatePropertyRequest("Land", "SA"));
        using var list = await client.GetAsync("/api/properties");
        using var getById = await client.GetAsync($"/api/properties/{property.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, getById.StatusCode);
    }

    private SmartPropertyApiFactory CreateFactory()
    {
        return new SmartPropertyApiFactory(
            fixture,
            usePersistencePermissionChecker: true,
            configuration: BootstrapDisabled);
    }

    private async Task<Property> SeedPropertyAsync()
    {
        var property = new Property(
            Guid.NewGuid(),
            PropertyType.Land,
            new PropertyAddress("SA"),
            DateTimeOffset.UtcNow.AddMinutes(-10));

        await using var context = fixture.CreateContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property;
    }
}
