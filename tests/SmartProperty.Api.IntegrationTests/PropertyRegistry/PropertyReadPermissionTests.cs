using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartProperty.Api.Contracts.PropertyRegistry;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using SmartProperty.Domain.PropertyRegistry;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

/// <summary>
/// Registry reads are scoped by role: an administrator (<c>property.update</c>) reads every property in every status,
/// anyone else signed in only the properties they registered, and a visitor none (the public sees listings instead).
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class PropertyReadPermissionTests(ApiPostgreSqlFixture fixture)
{
    private readonly AuthScenario scenario = new(fixture);
    private static readonly KeyValuePair<string, string?>[] BootstrapDisabled =
    [
        new("Bootstrap:Enabled", "false")
    ];

    [Fact]
    public async Task AVisitorReadsNoRegistryProperty()
    {
        var property = await SeedPropertyAsync(owner: null);
        using var factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true, configuration: BootstrapDisabled);
        using var anonymous = factory.CreateClient();

        using var list = await anonymous.GetAsync("/api/properties");
        using var getById = await anonymous.GetAsync($"/api/properties/{property.Id}");

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, getById.StatusCode);
    }

    [Fact]
    public async Task AnAdministratorReadsEveryPropertyInEveryStatus()
    {
        using var factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true, configuration: BootstrapDisabled);
        using var setup = factory.CreateClient();
        var admin = await scenario.RegisterActiveUserAsync(setup);
        var someoneElse = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(admin.UserId, PermissionCodes.PropertyUpdate);
        var active = await SeedPropertyAsync(someoneElse.UserId);
        var archived = await SeedPropertyAsync(someoneElse.UserId, archived: true);
        using var client = factory.CreateAuthenticatedClient(admin.UserId);

        var ids = await ListedIdsAsync(client, "/api/properties?pageSize=100");

        Assert.Contains(active.Id, ids);
        Assert.Contains(archived.Id, ids);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/properties/{archived.Id}")).StatusCode);
        Assert.DoesNotContain(active.Id, await ListedIdsAsync(client, "/api/properties?status=Archived&pageSize=100"));
    }

    [Fact]
    public async Task AnOwnerReadsOnlyTheirOwnPropertiesActiveAndArchived()
    {
        using var factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true, configuration: BootstrapDisabled);
        using var setup = factory.CreateClient();
        var owner = await scenario.RegisterActiveUserAsync(setup);
        var other = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(owner.UserId, PermissionCodes.PropertyCreate);
        await scenario.GrantPlatformPermissionAsync(owner.UserId, PermissionCodes.PropertyRead);
        var mine = await SeedPropertyAsync(owner.UserId);
        var mineArchived = await SeedPropertyAsync(owner.UserId, archived: true);
        var theirs = await SeedPropertyAsync(other.UserId);
        var unowned = await SeedPropertyAsync(owner: null);
        using var client = factory.CreateAuthenticatedClient(owner.UserId);

        var ids = await ListedIdsAsync(client, "/api/properties?pageSize=100");

        Assert.Equal(new[] { mine.Id, mineArchived.Id }.Order(), ids.Order());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/properties/{mine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/properties/{mineArchived.Id}")).StatusCode);

        // Someone else's property is indistinguishable from one that does not exist.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/properties/{theirs.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/properties/{unowned.Id}")).StatusCode);
    }

    [Fact]
    public async Task ASignedInUserWithoutPropertiesSeesAnEmptyList()
    {
        using var factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true, configuration: BootstrapDisabled);
        using var setup = factory.CreateClient();
        var user = await scenario.RegisterActiveUserAsync(setup);
        var other = await scenario.RegisterActiveUserAsync(setup);
        var theirs = await SeedPropertyAsync(other.UserId);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        Assert.Empty(await ListedIdsAsync(client, "/api/properties?pageSize=100"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/properties/{theirs.Id}")).StatusCode);
    }

    [Fact]
    public async Task PropertyReadAloneDoesNotAuthorizeCreate()
    {
        using var factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true, configuration: BootstrapDisabled);
        using var setup = factory.CreateClient();
        var user = await scenario.RegisterActiveUserAsync(setup);
        await scenario.GrantPlatformPermissionAsync(user.UserId, PermissionCodes.PropertyRead);
        using var client = factory.CreateAuthenticatedClient(user.UserId);

        using var response = await client.PostAsJsonAsync(
            "/api/properties",
            new CreatePropertyRequest("Land", Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<Guid[]> ListedIdsAsync(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();
    }

    private async Task<Property> SeedPropertyAsync(Guid? owner, bool archived = false)
    {
        var now = DateTimeOffset.UtcNow;
        var property = new Property(Guid.NewGuid(), PropertyType.Land, new PropertyAddress("SA"), now, ownerUserId: owner);

        if (archived)
        {
            property.Archive(now);
        }

        await using var context = fixture.CreateContext();
        context.Properties.Add(property);
        await context.SaveChangesAsync();

        return property;
    }
}
