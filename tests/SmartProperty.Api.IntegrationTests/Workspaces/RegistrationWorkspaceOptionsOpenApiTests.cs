using System.Text.Json;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.Workspaces;

/// <summary>
/// Reads the document the host actually generates, to confirm the registration options route is documented as an
/// anonymous GET returning an array of id/name options.
/// </summary>
[Collection(ApiPostgreSqlCollection.Name)]
public sealed class RegistrationWorkspaceOptionsOpenApiTests(ApiPostgreSqlFixture fixture)
{
    [Fact]
    public async Task TheRouteIsDocumentedAsAnAnonymousGetReturningAnArrayOfOptions()
    {
        using var factory = new SmartPropertyApiFactory(fixture);
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));

        var path = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/workspaces/registration-options");

        Assert.Equal(["get"], path.EnumerateObject().Select(property => property.Name));

        var operation = path.GetProperty("get");

        // The operation transformer adds a security requirement only to operations that require authorization.
        Assert.False(operation.TryGetProperty("security", out _));

        var schema = operation
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");

        Assert.Equal("array", schema.GetProperty("type").GetString());
        Assert.EndsWith(
            "/RegistrationWorkspaceOptionResponse",
            schema.GetProperty("items").GetProperty("$ref").GetString(),
            StringComparison.Ordinal);

        var properties = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("RegistrationWorkspaceOptionResponse")
            .GetProperty("properties");

        Assert.Equal(
            ["id", "name"],
            properties.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }
}
