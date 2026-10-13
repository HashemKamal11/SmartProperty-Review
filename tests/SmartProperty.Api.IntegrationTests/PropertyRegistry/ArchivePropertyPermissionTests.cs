using System.Net;
using System.Net.Http.Json;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

/// <summary>
/// Archive authorization through the production permission checker: only a platform-held <c>property.archive</c>
/// satisfies it, on its own, and no other property permission or workspace grant does.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class ArchivePropertyPermissionTests(ApiPostgreSqlFixture fixture)
{
    private readonly AuthScenario scenario = new(fixture);
    private static readonly KeyValuePair<string, string?>[] BootstrapDisabled =
    [
        new("Bootstrap:Enabled", "false")
    ];

    [Fact]
    public async Task PlatformPropertyArchiveAloneAuthorizesArchive()
    {
        var property = await SeedPropertyAsync();
        using var factory = CreateFactory();
        using var setup = factory.CreateClient();
        var user = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(user.UserId, PermissionCodes.PropertyArchive);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var response = await client.PostAsync($"/api/properties/{property.Id}/archive", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WorkspacePropertyArchiveDoesNotAuthorizeArchive()
    {
        var property = await SeedPropertyAsync();
        using var factory = CreateFactory();
        using var setup = factory.CreateClient();
        var user = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantWorkspacePermissionAsync(user.UserId, user.WorkspaceId, PermissionCodes.PropertyArchive);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var response = await client.PostAsync($"/api/properties/{property.Id}/archive", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(PermissionCodes.PropertyRead)]
    [InlineData(PermissionCodes.PropertyCreate)]
    [InlineData(PermissionCodes.PropertyUpdate)]
    public async Task OtherPropertyPermissionsDoNotAuthorizeArchive(string permissionCode)
    {
        var property = await SeedPropertyAsync();
        using var factory = CreateFactory();
        using var setup = factory.CreateClient();
        var user = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(user.UserId, permissionCode);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var response = await client.PostAsync($"/api/properties/{property.Id}/archive", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PropertyArchiveAloneDoesNotAuthorizeCreateReadOrUpdate()
    {
        var property = await SeedPropertyAsync();
        using var factory = CreateFactory();
        using var setup = factory.CreateClient();
        var user = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(user.UserId, PermissionCodes.PropertyArchive);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var create = await client.PostAsJsonAsync("/api/properties", new CreatePropertyRequest("Land", "SA"));
        using var list = await client.GetAsync("/api/properties");
        using var getById = await client.GetAsync($"/api/properties/{property.Id}");
        using var update = await client.PutAsJsonAsync(
            $"/api/properties/{property.Id}",
            new UpdatePropertyRequest("Unit", "SA"));

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, getById.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
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
