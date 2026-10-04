using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartProperty.Api.Contracts.Authentication;
using SmartProperty.Api.Contracts.Workspaces;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Domain.Identity;
using SmartProperty.Domain.Workspaces;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Workspaces;

/// <summary>
/// <c>GET /api/workspaces/registration-options</c> over real HTTP: anonymous access, the exact response shape, the
/// empty case, and a stable order.
/// </summary>
/// <remarks>
/// Tests that need to know the complete set of workspaces run on their own freshly migrated database; the
/// collection's shared database holds every other test's workspaces too, so there the tests only look for their own.
/// </remarks>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class RegistrationWorkspaceOptionsEndpointTests : IDisposable
{
    private const string Route = "/api/workspaces/registration-options";

    private readonly ApiPostgreSqlFixture _fixture;
    private readonly SmartPropertyApiFactory _factory;
    private readonly AuthScenario _scenario;

    public RegistrationWorkspaceOptionsEndpointTests(ApiPostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new SmartPropertyApiFactory(fixture);
        _scenario = new AuthScenario(fixture);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task AnAnonymousCaller_Gets200WithAPersistedWorkspaceIdAndName()
    {
        var workspaceId = await _scenario.CreateWorkspaceAsync();
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var options = (await response.Content.ReadFromJsonAsync<RegistrationWorkspaceOptionResponse[]>())!;
        var option = Assert.Single(options, candidate => candidate.Id == workspaceId);

        await using var context = _fixture.CreateContext();
        var workspace = await context.Workspaces.SingleAsync(candidate => candidate.Id == workspaceId);
        Assert.Equal(workspace.Name, option.Name);
    }

    [Fact]
    public async Task NoAuthenticationIsConsulted_AndTheBearerChallengeIsNeverIssued()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Equal(0, _factory.PermissionChecker.CallCount);
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("expired")]
    public async Task AStaleOrInvalidBearerToken_DoesNotBlockTheAnonymousEndpoint(string tokenKind)
    {
        // A browser can still hold an old token when it opens the registration page; that must not break it.
        var token = tokenKind == "expired" ? TestJwt.CreateExpiredAccessToken(Guid.NewGuid()) : tokenKind;
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NoWorkspacesExist_Returns200WithAnEmptyArray()
    {
        var connectionString = await CreateMigratedDatabaseAsync();
        using var factory = new SmartPropertyApiFactory(connectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EachOptionExposesExactlyIdAndName()
    {
        await _scenario.CreateWorkspaceAsync();
        using var client = _factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync(Route));

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.NotEmpty(document.RootElement.EnumerateArray());

        foreach (var element in document.RootElement.EnumerateArray())
        {
            Assert.Equal(
                ["id", "name"],
                element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task MultipleWorkspaces_AreReturnedOrderedByNameThenId_OnEveryCall()
    {
        var connectionString = await CreateMigratedDatabaseAsync();

        // The two equally named workspaces differ only in the first byte of the id, so PostgreSQL's uuid order and
        // the order asserted here cannot disagree.
        var zulu = new Workspace(Guid.NewGuid(), "Zulu Estates", DateTimeOffset.UtcNow);
        var alpha = new Workspace(Guid.NewGuid(), "Alpha Holdings", DateTimeOffset.UtcNow);
        var midHigh = new Workspace(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000"), "Mid", DateTimeOffset.UtcNow);
        var midLow = new Workspace(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000"), "Mid", DateTimeOffset.UtcNow);

        await using (var seeding = ApiPostgreSqlFixture.CreateContext(connectionString))
        {
            seeding.Workspaces.AddRange(zulu, alpha, midHigh, midLow);
            await seeding.SaveChangesAsync();
        }

        using var factory = new SmartPropertyApiFactory(connectionString);
        using var client = factory.CreateClient();

        var first = (await client.GetFromJsonAsync<RegistrationWorkspaceOptionResponse[]>(Route))!;
        var second = (await client.GetFromJsonAsync<RegistrationWorkspaceOptionResponse[]>(Route))!;

        Assert.Equal(
            [
                new RegistrationWorkspaceOptionResponse(alpha.Id, "Alpha Holdings"),
                new RegistrationWorkspaceOptionResponse(midLow.Id, "Mid"),
                new RegistrationWorkspaceOptionResponse(midHigh.Id, "Mid"),
                new RegistrationWorkspaceOptionResponse(zulu.Id, "Zulu Estates")
            ],
            first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task AReturnedWorkspaceId_RegistersAPendingUserWithAPendingAccessRequest()
    {
        var workspaceId = await _scenario.CreateWorkspaceAsync();
        using var client = _factory.CreateClient();

        var options = (await client.GetFromJsonAsync<RegistrationWorkspaceOptionResponse[]>(Route))!;
        var chosen = Assert.Single(options, option => option.Id == workspaceId);

        using var registration = await AuthScenario.PostRegisterAsync(
            client,
            AuthScenario.UniqueEmail("options"),
            chosen.Id);

        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);

        var body = (await registration.Content.ReadFromJsonAsync<RegisterResponse>())!;
        Assert.Equal(nameof(UserStatus.Pending), body.Status);
        Assert.Equal(chosen.Id, body.WorkspaceId);
        Assert.Equal(nameof(WorkspaceAccessRequestStatus.Pending), body.WorkspaceAccessStatus);
    }

    private async Task<string> CreateMigratedDatabaseAsync()
    {
        var connectionString = await _fixture.CreateBlankDatabaseAsync();

        await using var context = ApiPostgreSqlFixture.CreateContext(connectionString);
        await context.Database.MigrateAsync();

        return connectionString;
    }
}
