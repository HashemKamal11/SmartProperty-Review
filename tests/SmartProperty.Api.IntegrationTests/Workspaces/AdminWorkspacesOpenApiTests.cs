using System.Text.Json;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Workspaces;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class AdminWorkspacesOpenApiTests(ApiPostgreSqlFixture fixture)
{
    [Fact]
    public async Task RoutesResponsesSchemasAndBearerRequirementsAreDocumented()
    {
        using var factory = new SmartPropertyApiFactory(fixture, usePersistencePermissionChecker: true);
        using var client = factory.CreateClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = json.RootElement.GetProperty("paths");
        foreach (var (path, method, responses) in new[] {
            ("/api/admin/workspaces", "get", new[] { "200", "400", "401", "403" }),
            ("/api/admin/workspaces", "post", new[] { "201", "400", "401", "403", "422" }),
            ("/api/admin/workspaces/{workspaceId}", "get", new[] { "200", "401", "403", "404" }) })
        {
            var operation = paths.GetProperty(path).GetProperty(method);
            Assert.NotEmpty(operation.GetProperty("security").EnumerateArray());
            Assert.Equal(responses, operation.GetProperty("responses").EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
        }
        var schemas = json.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal(new[] { "createdAt", "id", "name", "updatedAt" },
            schemas.GetProperty("AdminWorkspaceResponse").GetProperty("properties").EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "name" },
            schemas.GetProperty("CreateWorkspaceRequest").GetProperty("properties").EnumerateObject().Select(x => x.Name));
        Assert.False(paths.GetProperty("/api/workspaces/registration-options").GetProperty("get").TryGetProperty("security", out _));
    }
}