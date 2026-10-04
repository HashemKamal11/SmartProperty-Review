using System.Net;
using System.Net.Http.Json;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class CreatePropertyPermissionTests(ApiPostgreSqlFixture fixture)
{
    private readonly ApiPostgreSqlFixture _fixture = fixture;
    private readonly AuthScenario _scenario = new(fixture);

    [Fact]
    public async Task FunctionalUserHoldingPlatformPermissionCanCreateProperty()
    {
        using var factory = new SmartPropertyApiFactory(_fixture, usePersistencePermissionChecker: true);
        using var registrationClient = factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(registrationClient);
        await _scenario.GrantPlatformPermissionAsync(user.UserId, PermissionCodes.PropertyCreate);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var response = await client.PostAsJsonAsync(
            "/api/properties",
            new CreatePropertyRequest("Land", "SA"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task SamePermissionHeldOnlyAtWorkspaceScopeDoesNotAuthorizePlatformCreate()
    {
        using var factory = new SmartPropertyApiFactory(_fixture, usePersistencePermissionChecker: true);
        using var registrationClient = factory.CreateClient();
        var user = await _scenario.RegisterActiveUserAsync(registrationClient);
        await _scenario.GrantWorkspacePermissionAsync(
            user.UserId,
            user.WorkspaceId,
            PermissionCodes.PropertyCreate);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var response = await client.PostAsJsonAsync(
            "/api/properties",
            new CreatePropertyRequest("Land", "SA"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
