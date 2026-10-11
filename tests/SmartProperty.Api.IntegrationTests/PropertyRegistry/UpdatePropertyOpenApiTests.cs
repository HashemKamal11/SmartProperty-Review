using System.Text.Json;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class UpdatePropertyOpenApiTests(ApiPostgreSqlFixture fixture)
{
    [Fact]
    public async Task OpenApiDocumentsTheUpdateOperationSecurityParametersBodyAndResponses()
    {
        using var factory = new SmartPropertyApiFactory(
            fixture,
            configuration:
            [
                new KeyValuePair<string, string?>("Bootstrap:Enabled", "false")
            ]);
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var path = document.RootElement.GetProperty("paths").GetProperty("/api/properties/{id}");

        Assert.False(path.TryGetProperty("patch", out _), "PATCH must not be exposed.");

        var update = path.GetProperty("put");

        Assert.Contains(
            update.GetProperty("security").EnumerateArray().SelectMany(value => value.EnumerateObject()),
            value => value.Name == "Bearer");

        var id = Assert.Single(update.GetProperty("parameters").EnumerateArray());
        Assert.Equal("id", id.GetProperty("name").GetString());
        Assert.Equal("path", id.GetProperty("in").GetString());
        Assert.True(id.GetProperty("required").GetBoolean());

        Assert.Contains(
            "UpdatePropertyRequest",
            update.GetProperty("requestBody").GetRawText(),
            StringComparison.Ordinal);

        var responses = update.GetProperty("responses");
        Assert.Equal(
            ["200", "400", "401", "403", "404", "409", "422"],
            responses.EnumerateObject().Select(response => response.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("PropertyResponse", responses.GetProperty("200").GetRawText(), StringComparison.Ordinal);

        var json = document.RootElement.GetRawText();
        Assert.DoesNotContain(PermissionCodes.PropertyUpdate, json, StringComparison.OrdinalIgnoreCase);
    }
}
