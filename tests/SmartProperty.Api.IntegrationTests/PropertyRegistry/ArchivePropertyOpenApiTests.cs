using System.Text.Json;
using SmartProperty.Api.IntegrationTests.Infrastructure;
using SmartProperty.Application.Authorization;
using Xunit;

namespace SmartProperty.Api.IntegrationTests.PropertyRegistry;

[Collection(ApiPostgreSqlCollection.Name)]
public sealed class ArchivePropertyOpenApiTests(ApiPostgreSqlFixture fixture)
{
    [Fact]
    public async Task OpenApiDocumentsTheArchiveOperationSecurityParameterAndResponsesWithoutABody()
    {
        using var factory = new SmartPropertyApiFactory(
            fixture,
            configuration:
            [
                new KeyValuePair<string, string?>("Bootstrap:Enabled", "false")
            ]);
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var path = document.RootElement.GetProperty("paths").GetProperty("/api/properties/{id}/archive");

        Assert.Equal(["post"], path.EnumerateObject().Select(operation => operation.Name).ToArray());

        var archive = path.GetProperty("post");

        Assert.Contains(
            archive.GetProperty("security").EnumerateArray().SelectMany(value => value.EnumerateObject()),
            value => value.Name == "Bearer");

        var id = Assert.Single(archive.GetProperty("parameters").EnumerateArray());
        Assert.Equal("id", id.GetProperty("name").GetString());
        Assert.Equal("path", id.GetProperty("in").GetString());
        Assert.True(id.GetProperty("required").GetBoolean());

        Assert.False(archive.TryGetProperty("requestBody", out _), "Archive takes no request body.");

        var responses = archive.GetProperty("responses");
        Assert.Equal(
            ["200", "401", "403", "404", "409"],
            responses.EnumerateObject().Select(response => response.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("PropertyResponse", responses.GetProperty("200").GetRawText(), StringComparison.Ordinal);

        var json = document.RootElement.GetRawText();
        Assert.DoesNotContain(PermissionCodes.PropertyArchive, json, StringComparison.OrdinalIgnoreCase);
    }
}
