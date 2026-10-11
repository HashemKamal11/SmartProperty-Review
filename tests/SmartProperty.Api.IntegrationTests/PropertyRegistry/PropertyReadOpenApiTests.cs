using System.Text.Json;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class PropertyReadOpenApiTests(ApiPostgreSqlFixture fixture)
{
    [Fact]
    public async Task OpenApiDocumentsBothReadOperationsSecurityParametersAndResponses()
    {
        using var factory = new SmartPropertyApiFactory(
            fixture,
            configuration:
            [
                new KeyValuePair<string, string?>("Bootstrap:Enabled", "false")
            ]);
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");

        var list = paths.GetProperty("/api/properties").GetProperty("get");
        AssertBearerSecurity(list);
        Assert.Equal(
            ["countryCode", "page", "pageSize", "status", "type"],
            list.GetProperty("parameters")
                .EnumerateArray()
                .Select(parameter => parameter.GetProperty("name").GetString()!)
                .Order(StringComparer.Ordinal)
                .ToArray());
        AssertResponses(list, "200", "400", "401", "403", "422");

        var getById = paths.GetProperty("/api/properties/{id}").GetProperty("get");
        AssertBearerSecurity(getById);
        var id = Assert.Single(getById.GetProperty("parameters").EnumerateArray());
        Assert.Equal("id", id.GetProperty("name").GetString());
        Assert.Equal("path", id.GetProperty("in").GetString());
        Assert.True(id.GetProperty("required").GetBoolean());
        AssertResponses(getById, "200", "401", "403", "404");

        var json = document.RootElement.GetRawText();
        Assert.Contains("PropertyResponse", json, StringComparison.Ordinal);
        Assert.Contains("PagedResponse", json, StringComparison.Ordinal);
        Assert.DoesNotContain(PermissionCodes.PropertyRead, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PermissionCodes.PropertyCreate, json, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertBearerSecurity(JsonElement operation)
    {
        Assert.Contains(
            operation.GetProperty("security").EnumerateArray().SelectMany(value => value.EnumerateObject()),
            value => value.Name == "Bearer");
    }

    private static void AssertResponses(JsonElement operation, params string[] expected)
    {
        var responses = operation.GetProperty("responses");

        foreach (var status in expected)
        {
            Assert.True(responses.TryGetProperty(status, out _), $"Response {status} was not documented.");
        }
    }
}
